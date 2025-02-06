using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FastMigrations.Runtime
{
    /// <summary>
    /// Variant of handling missing "Migrate_<see cref="MigratableAttribute.Version"/>(JObject data)" method
    /// </summary>
    /// <seealso cref="MigratorMissingMethodHandling"/>
    public enum MigratorMissingMethodHandling
    {
        /// <summary>Throws <see cref="MigrationException"/> if "Migrate_<see cref="MigratableAttribute.Version"/>(JObject data)" method doesn't exist on deserializable object</summary>
        ThrowException,
        /// <summary>Skips migration if "Migrate_<see cref="MigratableAttribute.Version"/>(JObject data)" method doesn't exist on deserializable object</summary>
        Ignore
    }

    internal delegate JObject MigrateMethod(JObject data);

    /*
     * Operational complexity (Co) = ???
     * Architectural complexity (Ca) = inputs + outputs + variables = ???
     * Cognitive complexity = Co * Ca = ???
     */
    public class FastMigrationsConverter : JsonConverter
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;

        private readonly MigratorMissingMethodHandling _methodHandling;

        private readonly ThreadLocal<HashSet<Type>> _migrationInProgress;
        private readonly IDictionary<Type, MigratableAttribute> _attributeByTypeCache;
        private readonly IDictionary<Type, IDictionary<int, MigrateMethod>> _migrateMethodsByType;
        
        /*
         * Operational complexity (Co) = ???
         * Architectural complexity (Ca) = inputs + outputs + variables = ???
         * Cognitive complexity = Co * Ca = ???
         */
        public FastMigrationsConverter(MigratorMissingMethodHandling methodHandling)
        {
            _migrationInProgress = new ThreadLocal<HashSet<Type>>(() => new HashSet<Type>());
            _attributeByTypeCache = new ConcurrentDictionary<Type, MigratableAttribute>();
            _migrateMethodsByType = new ConcurrentDictionary<Type, IDictionary<int, MigrateMethod>>();

            _methodHandling = methodHandling;
        }

        /*
         * Operational complexity (Co) = ???
         * Architectural complexity (Ca) = inputs + outputs + variables = ???
         * Cognitive complexity = Co * Ca = ???
         */
        public override bool CanConvert(Type objectType)
        {
            MigratableAttribute attribute = GetMigratableAttribute(objectType, _attributeByTypeCache);

            if (attribute == null)
                return false;

            if (attribute.Version == MigratorConstants.DefaultVersion)
                return false;

            return !_migrationInProgress.Value.Contains(objectType);
        }

        /*
         * Operational complexity (Co) = ???
         * Architectural complexity (Ca) = inputs + outputs + variables = ???
         * Cognitive complexity = Co * Ca = ???
         */
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            Type valueType = value.GetType();

            try
            {
                if (_migrationInProgress.Value.Contains(valueType))
                    return;

                _migrationInProgress.Value.Add(valueType);

                var jObject = JObject.FromObject(value, serializer);
                var migratableAttribute = GetMigratableAttribute(valueType, _attributeByTypeCache);
                jObject.Add(MigratorConstants.VersionJsonFieldName, migratableAttribute.Version);
                jObject.WriteTo(writer);
            }
            finally
            {
                _migrationInProgress.Value.Remove(valueType);
            }
        }

        /*
         * Operational complexity = 12228
         * Architectural complexity = inputs + outputs + variables = 4 + 1 + 4 + 9
         * Cognitive complexity = Oc * Ac = 12228 * 9 = 100052
         */
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            try // w = 2, if = 3 * (4074+2), W = 12228
            {
                if (_migrationInProgress.Value.Contains(objectType)) // w = 2, if = 3 * 3 = 9, func = 7, W = 2*(9+7) = 32
                    return existingValue; // w = 3 * seq = 3

                _migrationInProgress.Value.Add(objectType); // w = 2, seq = 1, func = 7, W = 2*(1+7)=16

                var jObject = JObject.Load(reader); // w = 2, seq = 1, func = 7, W = 2*(1+7)=16

                //don't try and repeat migration for objects serialized as refs to previous
                if (jObject["$ref"] != null) // w = 2, if = 3 * 288 = 864, W = 2 * 864 = 1728                                                   
                    if (serializer.ReferenceResolver != null) // w = 3, if = 3 * (28 + 4) = 96, W = 3 * 96 = 288
                        return serializer.ReferenceResolver.ResolveReference(serializer, (string)jObject["$ref"]); // w = 4, func = 7, W = 28
                    else
                        return null; // w = 4, seq = 1, W = 4

                int fromVersion = MigratorConstants.DefaultVersion; // w = 2, seq = 1, W = 2

                if (jObject.ContainsKey(MigratorConstants.VersionJsonFieldName)) // w = 2, if = 3 * 24 = 72, func = 7, W = 2*(72+7) = 158
                    fromVersion = jObject[MigratorConstants.VersionJsonFieldName].ToObject<int>(); // w = 3, seq = 1, func = 7, W = 3*(1+7)=24

                var migratableAttribute = GetMigratableAttribute(objectType, _attributeByTypeCache); // w = 2, seq = 1, func = 7, W = 2*(1+7)=16
                uint toVersion = migratableAttribute.Version;                                        // w = 2, seq = 1, W = 2

                if (toVersion + fromVersion != 0 && fromVersion != toVersion)                              // w = 2, if = 3 * 24 = 72, W = 2*72 = 144
                    jObject = RunMigrations(jObject, objectType, fromVersion, toVersion, _methodHandling); // w = 3, seq = 1, func = 7, W = 3*(1+7)=24

                if (existingValue != null && serializer.ObjectCreationHandling != ObjectCreationHandling.Replace) // w=2, if = 3 * 324 = 972, W = 972 * 2 = 1944
                {
                    using (JsonReader jObjReader = jObject.CreateReader()) // w = 3, if = 3 * (32+4) = 108, W = 3 * 108 = 324
                    {
                        serializer.Populate(jObjReader, existingValue); // w = 4, seq, func, W = 4*(1+7)=32
                        return existingValue; // w = 4, seq, W = 4
                    }
                }

                return jObject.ToObject(objectType, serializer); // w = 2, seq = 1, func = 7, W = 2*(1+7)=16
            }
            finally
            {
                _migrationInProgress.Value.Remove(objectType); // w = 2, seq, W = 2
            }
        }

        /*
         * Operational complexity (Co) = ???
         * Architectural complexity (Ca) = inputs + outputs + variables = ???
         * Cognitive complexity = Co * Ca = ???
         */
        private JObject RunMigrations(JObject jObject, Type objectType, int fromVersion,
            uint toVersion, MigratorMissingMethodHandling methodHandling)
        {
            fromVersion += MigratorConstants.MinVersionToStartMigration;

            for (int currVersion = fromVersion; currVersion <= toVersion; ++currVersion)
            {
                var migrationMethod = GetMigrateMethod(objectType, currVersion, _migrateMethodsByType);

                if (migrationMethod == null)
                {
                    switch (methodHandling)
                    {
                        case MigratorMissingMethodHandling.ThrowException:
                        {
                            var methodName = string.Format(MigratorConstants.MigrateMethodFormat, currVersion);
                            throw new MigrationException($"Migration method {methodName} not found in {objectType.Name}");
                        }
                        case MigratorMissingMethodHandling.Ignore:
                        {
                            continue;
                        }
                    }
                }

                jObject = migrationMethod(jObject);
            }
            return jObject;
        }

        /*
         * Operational complexity (Co) = ???
         * Architectural complexity (Ca) = inputs + outputs + variables = ???
         * Cognitive complexity = Co * Ca = ???
         */
        private static MigratableAttribute GetMigratableAttribute(Type objectType, IDictionary<Type, MigratableAttribute> cache)
        {
            if (cache.TryGetValue(objectType, out MigratableAttribute attribute))
                return attribute;

            attribute = (MigratableAttribute)objectType.GetCustomAttribute(typeof(MigratableAttribute), false);
            cache[objectType] = attribute;
            return attribute;
        }

        /*
         * Operational complexity (Co) = ???
         * Architectural complexity (Ca) = inputs + outputs + variables = ???
         * Cognitive complexity = Co * Ca = ???
         */
        private static MigrateMethod GetMigrateMethod(Type objectType, int version, IDictionary<Type, IDictionary<int, MigrateMethod>> cache)
        {
            if (!cache.TryGetValue(objectType, out IDictionary<int, MigrateMethod> methodsByVersion))
            {
                methodsByVersion = new ConcurrentDictionary<int, MigrateMethod>();
                cache[objectType] = methodsByVersion;
            }

            if (methodsByVersion.TryGetValue(version, out MigrateMethod method))
                return method;

            var methodName = string.Format(MigratorConstants.MigrateMethodFormat, version);
            var methodInfo = objectType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);

            if (methodInfo == null)
                return null;

            MigrateMethod newMethodDelegate = (MigrateMethod)methodInfo.CreateDelegate(typeof(MigrateMethod));
            methodsByVersion[version] = newMethodDelegate;
            return newMethodDelegate;
        }
    }
}