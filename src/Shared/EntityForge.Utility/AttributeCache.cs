using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Caches reflected attributes for repeated type or member inspection.</summary>
    public static class AttributeCache
    {
        private static readonly Dictionary<Type, List<Attribute>> s_cachedAttributes = new Dictionary<Type, List<Attribute>>();

        /// <summary>Attempts to retrieve the requested attribute from the reflection cache.</summary>
        public static TAttribute TryGetAttribute<TAttribute>(Type attributedType)
            where TAttribute : Attribute
        {
            if (attributedType == null)
            {
                throw new ArgumentNullException(nameof(attributedType));
            }

            TAttribute attribute = null;

            List<Attribute> attributes;
            if (!s_cachedAttributes.TryGetValue(attributedType, out attributes))
            {
                attributes = new List<Attribute>();
                s_cachedAttributes.Add(attributedType, attributes);

                object[] customAttributes = attributedType.GetCustomAttributes(typeof(TAttribute), true);
                attribute = customAttributes.OfType<TAttribute>().FirstOrDefault();

                attributes.Add(attribute);
            }
            else
            {
                attribute = attributes.OfType<TAttribute>().FirstOrDefault();
            }

            return attribute;
        }
    }
}
