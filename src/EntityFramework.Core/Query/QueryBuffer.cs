﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Data.Entity.ChangeTracking;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Utilities;

namespace Microsoft.Data.Entity.Query
{
    public class QueryBuffer : IQueryBuffer
    {
        private readonly StateManager _stateManager;
        private readonly EntityKeyFactorySource _entityKeyFactorySource;
        private readonly EntityMaterializerSource _materializerSource;
        private readonly ClrCollectionAccessorSource _clrCollectionAccessorSource;
        private readonly ClrPropertySetterSource _clrPropertySetterSource;

        private sealed class BufferedEntity
        {
            private readonly IEntityType _entityType;
            private readonly IValueReader _valueReader; // TODO: This only works with buffering value readers

            public BufferedEntity(IEntityType entityType, IValueReader valueReader)
            {
                _entityType = entityType;
                _valueReader = valueReader;
            }

            public object Instance { get; set; }

            public IValueReader ValueReader
            {
                get { return _valueReader; }
            }

            public void StartTracking(StateManager stateManager)
            {
                stateManager.StartTracking(_entityType, Instance, _valueReader);
            }
        }

        private readonly Dictionary<EntityKey, BufferedEntity> _byEntityKey
            = new Dictionary<EntityKey, BufferedEntity>();

        private readonly IDictionary<object, List<BufferedEntity>> _byEntityInstance
            = new Dictionary<object, List<BufferedEntity>>();

        public QueryBuffer(
            [NotNull] StateManager stateManager,
            [NotNull] EntityKeyFactorySource entityKeyFactorySource,
            [NotNull] EntityMaterializerSource materializerSource,
            [NotNull] ClrCollectionAccessorSource clrCollectionAccessorSource,
            [NotNull] ClrPropertySetterSource clrPropertySetterSource)
        {
            Check.NotNull(stateManager, "stateManager");
            Check.NotNull(entityKeyFactorySource, "entityKeyFactorySource");
            Check.NotNull(materializerSource, "materializerSource");
            Check.NotNull(clrCollectionAccessorSource, "clrCollectionAccessorSource");
            Check.NotNull(clrPropertySetterSource, "clrPropertySetterSource");

            _stateManager = stateManager;
            _entityKeyFactorySource = entityKeyFactorySource;
            _materializerSource = materializerSource;
            _clrCollectionAccessorSource = clrCollectionAccessorSource;
            _clrPropertySetterSource = clrPropertySetterSource;
        }

        public virtual object GetEntity(IEntityType entityType, IValueReader valueReader)
        {
            return GetEntity(entityType, valueReader, queryStateManager: true);
        }

        public virtual object GetEntity(IEntityType entityType, IValueReader valueReader, bool queryStateManager)
        {
            Check.NotNull(entityType, "entityType");
            Check.NotNull(valueReader, "valueReader");

            var keyProperties
                = entityType.GetPrimaryKey().Properties;

            var entityKey
                = _entityKeyFactorySource
                    .GetKeyFactory(keyProperties)
                    .Create(entityType, keyProperties, valueReader);

            if (entityKey == EntityKey.NullEntityKey)
            {
                return null;
            }

            var stateEntry = _stateManager.TryGetEntry(entityKey);

            if (queryStateManager && stateEntry != null)
            {
                return stateEntry.Entity;
            }

            BufferedEntity bufferedEntity;
            if (!_byEntityKey.TryGetValue(entityKey, out bufferedEntity))
            {
                bufferedEntity
                    = new BufferedEntity(entityType, valueReader)
                        {
                            // TODO: Optimize this by not materializing when not required for query execution. i.e.
                            //       entity is only needed in final results
                            Instance = _materializerSource.GetMaterializer(entityType)(valueReader)
                        };

                _byEntityKey.Add(entityKey, bufferedEntity);
                _byEntityInstance.Add(bufferedEntity.Instance, new List<BufferedEntity> { bufferedEntity });
            }

            return bufferedEntity.Instance;
        }

        public virtual object GetPropertyValue(object entity, IProperty property)
        {
            Check.NotNull(entity, "entity");
            Check.NotNull(property, "property");

            var stateEntry = _stateManager.TryGetEntry(entity);

            return stateEntry != null
                ? stateEntry[property]
                : _byEntityInstance[entity][0].ValueReader
                    .ReadValue<object>(property.Index);
        }

        public virtual void StartTracking(object entity)
        {
            Check.NotNull(entity, "entity");

            List<BufferedEntity> bufferedEntities;
            if (_byEntityInstance.TryGetValue(entity, out bufferedEntities))
            {
                foreach (var bufferedEntity in bufferedEntities)
                {
                    bufferedEntity.StartTracking(_stateManager);
                }
            }
        }

        public virtual void Include(
            object entity,
            INavigation navigation,
            Func<EntityKey, Func<IValueReader, EntityKey>, IEnumerable<IValueReader>> relatedValueReaders)
        {
            Check.NotNull(navigation, "navigation");
            Check.NotNull(relatedValueReaders, "relatedValueReaders");

            if (entity == null)
            { 
                return;
            }

            EntityKey primaryKey;
            List<BufferedEntity> bufferedEntities;
            Func<IValueReader, EntityKey> relatedKeyFactory;

            var targetEntityType
                = IncludeCore(entity, navigation, out primaryKey, out bufferedEntities, out relatedKeyFactory);

            LoadNavigationProperties(
                entity,
                navigation,
                relatedValueReaders(primaryKey, relatedKeyFactory)
                    .Select(valueReader => GetTargetEntity(targetEntityType, valueReader, bufferedEntities))
                    .Where(e => e != null)
                    .ToList());
        }

        public virtual async Task IncludeAsync(
            object entity,
            INavigation navigation,
            Func<EntityKey, Func<IValueReader, EntityKey>, IAsyncEnumerable<IValueReader>> relatedValueReaders,
            CancellationToken cancellationToken)
        {
            Check.NotNull(navigation, "navigation");
            Check.NotNull(relatedValueReaders, "relatedValueReaders");

            if (entity == null)
            { 
                return;
            }

            EntityKey primaryKey;
            List<BufferedEntity> bufferedEntities;
            Func<IValueReader, EntityKey> relatedKeyFactory;

            var targetEntityType
                = IncludeCore(entity, navigation, out primaryKey, out bufferedEntities, out relatedKeyFactory);

            LoadNavigationProperties(
                entity,
                navigation,
                await relatedValueReaders(primaryKey, relatedKeyFactory)
                    .Select(valueReader => GetTargetEntity(targetEntityType, valueReader, bufferedEntities))
                    .Where(e => e != null)
                    .ToList(cancellationToken)
                    .WithCurrentCulture());
        }

        private IEntityType IncludeCore(
            object entity,
            INavigation navigation,
            out EntityKey primaryKey,
            out List<BufferedEntity> bufferedEntities,
            out Func<IValueReader, EntityKey> relatedKeyFactory)
        {
            var primaryKeyFactory
                = _entityKeyFactorySource
                    .GetKeyFactory(navigation.ForeignKey.ReferencedProperties);

            var foreignKeyFactory
                = _entityKeyFactorySource
                    .GetKeyFactory(navigation.ForeignKey.Properties);

            var targetEntityType = navigation.GetTargetType();

            if (!_byEntityInstance.TryGetValue(entity, out bufferedEntities))
            {
                _byEntityInstance.Add(entity, bufferedEntities = new List<BufferedEntity>());

                var stateEntry = _stateManager.TryGetEntry(entity);

                Debug.Assert(stateEntry != null);

                primaryKey
                    = navigation.PointsToPrincipal
                        ? stateEntry.GetDependentKeySnapshot(navigation.ForeignKey)
                        : stateEntry.GetPrimaryKeyValue();
            }
            else
            {
                primaryKey
                    = navigation.PointsToPrincipal
                        ? foreignKeyFactory
                            .Create(
                                targetEntityType,
                                navigation.ForeignKey.Properties,
                                bufferedEntities[0].ValueReader)
                        : primaryKeyFactory
                            .Create(
                                navigation.EntityType,
                                navigation.ForeignKey.ReferencedProperties,
                                bufferedEntities[0].ValueReader);
            }

            if (navigation.PointsToPrincipal)
            {
                relatedKeyFactory
                    = valueReader =>
                        primaryKeyFactory
                            .Create(
                                targetEntityType,
                                navigation.ForeignKey.ReferencedProperties,
                                valueReader);
            }
            else
            {
                relatedKeyFactory
                    = valueReader =>
                        foreignKeyFactory
                            .Create(
                                navigation.EntityType,
                                navigation.ForeignKey.Properties,
                                valueReader);
            }

            return targetEntityType;
        }

        private void LoadNavigationProperties(
            object entity, INavigation navigation, IReadOnlyList<object> relatedEntities)
        {
            if (navigation.PointsToPrincipal
                && relatedEntities.Any())
            {
                _clrPropertySetterSource
                    .GetAccessor(navigation)
                    .SetClrValue(entity, relatedEntities[0]);

                var inverseNavigation = navigation.TryGetInverse();

                if (inverseNavigation != null)
                {
                    if (inverseNavigation.IsCollection())
                    {
                        _clrCollectionAccessorSource
                            .GetAccessor(inverseNavigation)
                            .AddRange(relatedEntities[0], new[] { entity });
                    }
                    else
                    {
                        _clrPropertySetterSource
                            .GetAccessor(inverseNavigation)
                            .SetClrValue(relatedEntities[0], entity);
                    }
                }
            }
            else
            {
                if (navigation.IsCollection())
                {
                    _clrCollectionAccessorSource
                        .GetAccessor(navigation)
                        .AddRange(entity, relatedEntities);

                    var inverseNavigation = navigation.TryGetInverse();

                    if (inverseNavigation != null)
                    {
                        var clrPropertySetter
                            = _clrPropertySetterSource
                                .GetAccessor(inverseNavigation);

                        foreach (var relatedEntity in relatedEntities)
                        {
                            clrPropertySetter.SetClrValue(relatedEntity, entity);
                        }
                    }
                }
                else if (relatedEntities.Any())
                {
                    _clrPropertySetterSource
                        .GetAccessor(navigation)
                        .SetClrValue(entity, relatedEntities[0]);

                    var inverseNavigation = navigation.TryGetInverse();

                    if (inverseNavigation != null)
                    {
                        _clrPropertySetterSource
                            .GetAccessor(inverseNavigation)
                            .SetClrValue(relatedEntities[0], entity);
                    }
                }
            }
        }

        private object GetTargetEntity(
            IEntityType targetEntityType, IValueReader valueReader, ICollection<BufferedEntity> bufferedEntities)
        {
            var targetEntity = GetEntity(targetEntityType, valueReader);

            if (targetEntity != null)
            {
                List<BufferedEntity> bufferedTargetEntities;
                bufferedEntities.Add(
                    _byEntityInstance.TryGetValue(targetEntity, out bufferedTargetEntities)
                        ? bufferedTargetEntities[0]
                        : new BufferedEntity(targetEntityType, valueReader)
                            {
                                Instance = targetEntity
                            });
            }

            return targetEntity;
        }
    }
}
