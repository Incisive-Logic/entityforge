using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Creates and raises property notification callbacks from names or expressions.</summary>
    public static class NotifyPropertyChangedHelper
    {
        /// <summary>Extracts a property name from the supplied member expression.</summary>
        public static string GetPropertyName<T>(Expression<Func<T>> propertyExpression)
        {
            var body = propertyExpression.Body as MemberExpression;
            if (body == null)
            {
                throw new ArgumentException("'propertyExpression' should be a member expression");
            }

            var expression = body.Expression as ConstantExpression;
            if (expression == null)
            {
                throw new ArgumentException("'propertyExpression' body should be a constant expression");
            }

            return body.Member.Name;
        }

        /// <summary>Compiles a delegate for the supplied property expression.</summary>
        public static Delegate GetCompiledDelegate<T>(Expression<Func<T>> propertyExpression, out string propertyName)
        {
            propertyName = GetPropertyName<T>(propertyExpression);
            return Expression.Lambda(propertyExpression).Compile();
        }

        /// <summary>Raises a property-changed notification using the supplied sender and property selection.</summary>
        public static void RaisePropertyChanged<T>(this PropertyChangedEventHandler handler, Expression<Func<T>> propertyExpression)
        {
            if (handler != null)
            {
                string propertyName;
                object target = GetCompiledDelegate<T>(propertyExpression, out propertyName).DynamicInvoke();

                var e = new PropertyChangedEventArgs(propertyName);
                handler(target, e);
            }
        }

        /// <summary>Raises a property-changed notification using the supplied sender and property selection.</summary>
        public static void RaisePropertyChanged<T>(this PropertyChangedEventHandler handler, params Expression<Func<T>>[] propertyExpressions)
        {
            foreach (var propertyExpression in propertyExpressions)
            {
                handler.RaisePropertyChanged<T>(propertyExpression);
            }
        }

        /// <summary>Raises a property-changing notification before the selected property is changed.</summary>
        public static void RaisePropertyChanging<T>(this PropertyChangingEventHandler handler, Expression<Func<T>> propertyExpression)
        {
            if (handler != null)
            {
                string propertyName;
                object target = GetCompiledDelegate<T>(propertyExpression, out propertyName).DynamicInvoke();

                var e = new PropertyChangingEventArgs(propertyName);
                handler(target, e);
            }
        }

        /// <summary>Raises a property-changing notification before the selected property is changed.</summary>
        public static void RaisePropertyChanging<T>(this PropertyChangingEventHandler handler, params Expression<Func<T>>[] propertyExpressions)
        {
            foreach (var propertyExpression in propertyExpressions)
            {
                handler.RaisePropertyChanging<T>(propertyExpression);
            }
        }
    }
}
