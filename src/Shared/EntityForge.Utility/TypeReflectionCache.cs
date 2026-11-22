using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>
    /// Caches reflection metadata for CLR types
    /// </summary>
    public static class TypeReflectionCache
    {
        private static readonly Dictionary<Type, PropertyInfo[]> s_typeReflectionCache = new Dictionary<Type, PropertyInfo[]>();
        private static readonly Dictionary<Tuple<Type, string>, Delegate> s_propertyDelegateCache = new Dictionary<Tuple<Type, string>, Delegate>();
        private static readonly List<Type> s_assemblyClassTypes = new List<Type>();
        private static readonly Dictionary<Type, HashSet<Type>> s_superClassTypesByType = new Dictionary<Type, HashSet<Type>>();

        private static readonly object s_cacheLock = new object();
        private static bool s_isLoaded;

        /// <summary>On the first call, caches class types from assemblies already loaded in the current application domain.</summary>
        public static void LoadTypes()
        {
            lock (s_cacheLock)
            {
                if (!s_isLoaded)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        s_assemblyClassTypes.AddRange(assembly.GetTypes().Where(t => t.IsClass));
                    }

                    s_isLoaded = true;
                }
            }
        }

        /// <summary>
        /// Gets the reflected property information associated with a CLR type
        /// </summary>
        /// <param name="type">The type to reflect over</param>
        /// <returns>The reflected property information</returns>
        public static PropertyInfo[] GetProperties(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            PropertyInfo[] propertyInfos = null;

            if (!s_typeReflectionCache.TryGetValue(type, out propertyInfos))
            {
                propertyInfos = type.GetProperties();
                s_typeReflectionCache.Add(type, propertyInfos);
            }

            return propertyInfos;
        }

        private static Delegate ToDelegate(MethodInfo mi, object target)
        {
            if (mi == null)
            {
                throw new ArgumentNullException("mi");
            }

            Type delegateType;

            var typeArgs = mi.GetParameters()
                .Select(p => p.ParameterType)
                .ToList();

            // builds a delegate type
            if (mi.ReturnType == typeof(void))
            {
                delegateType = Expression.GetActionType(typeArgs.ToArray());

            }
            else
            {
                typeArgs.Add(mi.ReturnType);
                delegateType = Expression.GetFuncType(typeArgs.ToArray());
            }

            // creates a binded delegate if target is supplied
            var result = (target == null)
                ? Delegate.CreateDelegate(delegateType, mi)
                : Delegate.CreateDelegate(delegateType, target, mi);

            return result;
        }

        /// <summary>
        /// Returns a compiled and cached get property delegate for a CLR type
        /// </summary>
        /// <param name="clrObject">An object whose runtime type contains the requested property.</param>
        /// <param name="propertyPath">A single property name, matched without regard to case; nested paths are not supported.</param>
        /// <returns>The cached getter delegate.</returns>
        /// <remarks>The current delegate construction supports static, parameterless getters. It does not bind instance getters to clrObject. Cache access is not synchronized.</remarks>
        /// <exception cref="ArgumentNullException">clrObject is null, or the selected property has no public getter.</exception>
        /// <exception cref="ArgumentException">propertyPath is empty, or the getter cannot bind to the generated delegate signature.</exception>
        public static Delegate GetPropertyGetDelegate(object clrObject, string propertyPath)
        {
            if (clrObject == null)
            {
                throw new ArgumentNullException(nameof(clrObject));
            }

            if (string.IsNullOrEmpty(propertyPath))
            {
                throw new ArgumentException(nameof(propertyPath));
            }

            Tuple<Type, string> key = Tuple.Create(clrObject.GetType(), propertyPath);

            Delegate getPropertyDelegate = null;
            if (!s_propertyDelegateCache.TryGetValue(key, out getPropertyDelegate))
            {
                PropertyInfo propertyInfo = GetProperties(clrObject.GetType()).Where(i => i.Name.Equals(propertyPath, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                // Return an unbound delegate, not specific to a particular object instance
                getPropertyDelegate = ToDelegate(propertyInfo.GetGetMethod(), null);
                s_propertyDelegateCache.Add(key, getPropertyDelegate);
            }

            return getPropertyDelegate;
        }

        /// <summary>
        /// Invokes the cached property getter without arguments.
        /// </summary>
        /// <param name="clrObject">An object used to select the declaring runtime type.</param>
        /// <param name="propertyPath">A single property name, matched without regard to case.</param>
        /// <returns>The value returned by the static, parameterless getter.</returns>
        /// <remarks>Inherits the binding and synchronization limitations of <see cref="GetPropertyGetDelegate"/>. This helper does not support reading instance properties.</remarks>
        public static object GetPropertyValue(object clrObject, string propertyPath)
        {
            Delegate getPropertyDelegate = GetPropertyGetDelegate(clrObject, propertyPath);
            return getPropertyDelegate.DynamicInvoke();
        }

        /// <summary>Returns cached assignable class types, including the supplied type itself, from the loaded-assembly snapshot.</summary>
        public static HashSet<Type> GetBaseTypes(Type subClassType)
        {
            HashSet<Type> superClassTypes;

            lock (s_cacheLock)
            {
                LoadTypes();

                if (!s_superClassTypesByType.TryGetValue(subClassType, out superClassTypes))
                {
                    superClassTypes = new HashSet<Type>();
                    foreach (Type superClassType in s_assemblyClassTypes.Where(t => t.IsClass && t != typeof(object) && t.IsAssignableFrom(subClassType)))
                    {
                        superClassTypes.Add(superClassType);
                    }

                    // Include the subclass type also
                    superClassTypes.Add(subClassType);

                    s_superClassTypesByType.Add(subClassType, superClassTypes);
                }
            }

            return superClassTypes;
        }
    }
}
