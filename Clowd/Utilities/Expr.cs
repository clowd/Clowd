using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Utilities
{
    public static class Expr
    {
        public static PropertyInfo GetPropertyInfoFromExpression<TSource, TProperty>(Expression<Func<TSource, TProperty>> propertyLambda)
        {
            Type type = typeof(TSource);

            MemberExpression member = propertyLambda.Body as MemberExpression;
            if (member == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a method, not a property.",
                    propertyLambda.ToString()));

            PropertyInfo propInfo = member.Member as PropertyInfo;
            if (propInfo == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a field, not a property.",
                    propertyLambda.ToString()));

            if (type != propInfo.ReflectedType &&
                !type.IsSubclassOf(propInfo.ReflectedType))
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a property that is not from type {1}.",
                    propertyLambda.ToString(),
                    type));

            return propInfo;
        }

        public static FieldInfo GetFieldInfoFromExpression<TSource, TProperty>(Expression<Func<TSource, TProperty>> fieldLambda)
        {
            Type type = typeof(TSource);

            MemberExpression member = fieldLambda.Body as MemberExpression;
            if (member == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a method, not a field.",
                    fieldLambda.ToString()));

            FieldInfo fieldInfo = member.Member as FieldInfo;
            if (fieldInfo == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' does not refer to a field.",
                    fieldLambda.ToString()));

            if (type != fieldInfo.ReflectedType &&
                !type.IsSubclassOf(fieldInfo.ReflectedType))
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a field that is not from type {1}.",
                    fieldLambda.ToString(),
                    type));

            return fieldInfo;
        }
    }
}
