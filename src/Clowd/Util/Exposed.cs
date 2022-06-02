// Author:
// Leszek Ciesielski (skolima@gmail.com)
// Manuel Josupeit-Walter (josupeit-walter@cis-gmbh.de)
//
// (C) 2013 Cognifide
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;

namespace Clowd.Util
{
    public class Exposed : DynamicObject
    {
        /// <summary>
        /// The <see langword="object"/> that is being exposed.
        /// <see langword="null"/> if static members of a <see cref="Type"/> are being exposed.
        /// </summary>
        private readonly object value;

        /// <summary>
        /// Initializes a new instance of the <see cref="Exposed"/> class. 
        /// Creates a new wrapper for accessing members of subject.
        /// </summary>
        /// <param name="subject">
        /// The object which will have it's members exposed.
        /// </param>
        /// <returns>
        /// A new wrapper around the subject.
        /// </returns>
        private Exposed(object subject)
        {
            value = subject;
            SubjectType = subject.GetType();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Exposed"/> class. 
        /// Creates a new wrapper for accessing hidden static members of a <see cref="Type"/>.
        /// </summary>
        /// <param name="type">
        /// The <see cref="Type"/> which will have it's static members exposed.
        /// </param>
        /// <returns>
        /// A new wrapper around a <see cref="Type"/>.
        /// </returns>
        private Exposed(Type type)
        {
            SubjectType = type;
        }

        /// <summary>
        /// Gets the <see cref="Type"/> of the exposed object.
        /// </summary>
        private Type SubjectType { get; set; }

        /// <summary>
        /// Creates a new wrapper for accessing members of subject.
        /// </summary>
        /// <param name="subject">
        /// The object which will have it's members exposed.
        /// </param>
        /// <returns>
        /// A new wrapper around the subject.
        /// </returns>
        public static dynamic From(object subject)
        {
            return new Exposed(subject);
        }

        /// <summary>
        /// Creates a new wrapper for accessing hidden static members of a <see cref="Type"/>.
        /// </summary>
        /// <param name="type">
        /// The <see cref="Type"/> which will have it's static members exposed.
        /// </param>
        /// <returns>
        /// A new wrapper around a <see cref="Type"/>.
        /// </returns>
        public static dynamic From(Type type)
        {
            return new Exposed(type);
        }

        /// <summary>
        /// Creates a new wrapper for accessing members of a new instance of <see cref="Type"/>.
        /// </summary>
        /// <param name="type">
        /// The <see cref="Type"/> of which an instance will have it's members exposed.
        /// </param>
        /// <returns>
        /// A new wrapper around a new instance of <see cref="Type"/>.
        /// </returns>
        public static dynamic New(Type type)
        {
            return new Exposed(Activator.CreateInstance(type));
        }

        /// <summary>
        /// Returns the <see cref="DynamicMetaObject"/> responsible for binding operations performed on this object.
        /// </summary>
        /// <param name="parameter">
        /// The expression tree representation of the runtime value.
        /// </param>
        /// <returns>
        /// The <see cref="DynamicMetaObject"/> to bind this object.
        /// </returns>
        public override DynamicMetaObject GetMetaObject(Expression parameter)
        {
            return new MetaObject(parameter, this, value == null);
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            if (indexes.Length != 1)
                throw new InvalidOperationException("Exposed indexing expression must only have one index");

            var memberName = indexes[0] as string;
            if (string.IsNullOrWhiteSpace(memberName))
                throw new InvalidOperationException("Exposed indexing expression must be a non-null string");

            var member = GetMemberInfo(memberName);
            result = GetMemberValue(member, this.value);
            return true;
        }

        public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object newValue)
        {
            if (indexes.Length != 1)
                throw new InvalidOperationException("Exposed indexing expression must only have one index");

            var memberName = indexes[0] as string;
            if (string.IsNullOrWhiteSpace(memberName))
                throw new InvalidOperationException("Exposed indexing expression must be a non-null string");

            var member = GetMemberInfo(memberName);
            SetMemberValue(member, this.value, newValue);
            return true;
        }

        private MemberInfo GetMemberInfo(string memberName)
        {
            var declaringType = SubjectType;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (value == null ? BindingFlags.Static : BindingFlags.Instance);
            MemberInfo member = null;

            do
            {
                var property = declaringType.GetProperty(memberName, flags);
                if (property != null)
                {
                    member = property;
                }
                else
                {
                    var field = declaringType.GetField(memberName, flags);
                    if (field != null)
                    {
                        member = field;
                    }
                }
            } while (member == null && (declaringType = declaringType.BaseType) != null);

            if (member == null)
                throw new MissingMemberException(SubjectType.Name, memberName);

            return member;
        }

        public static object GetMemberValue(MemberInfo memberInfo, object forObject)
        {
            switch (memberInfo.MemberType)
            {
                case MemberTypes.Field:
                    return ((FieldInfo)memberInfo).GetValue(forObject);
                case MemberTypes.Property:
                    return ((PropertyInfo)memberInfo).GetValue(forObject);
                default:
                    throw new NotImplementedException();
            }
        }

        public static void SetMemberValue(MemberInfo memberInfo, object forObject, object newValue)
        {
            switch (memberInfo.MemberType)
            {
                case MemberTypes.Field:
                    ((FieldInfo)memberInfo).SetValue(forObject, newValue);
                    return;
                case MemberTypes.Property:
                    ((PropertyInfo)memberInfo).SetValue(forObject, newValue);
                    return;
                default:
                    throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Represents the dynamic binding and a binding logic of an object participating in the dynamic binding.
        /// </summary>
        private sealed class MetaObject : ProxyMetaObject
        {
            /// <summary>
            /// Should this <see cref="MetaObject"/> bind to <see langword="static"/> or instance methods and fields.
            /// </summary>
            private readonly bool isStatic;

            /// <summary>
            /// Initializes a new instance of the <see cref="MetaObject"/> class.
            /// </summary>
            /// <param name="expression">
            /// The expression representing this <see cref="DynamicMetaObject"/> during the dynamic binding process.
            /// </param>
            /// <param name="value">
            /// The runtime value represented by the <see cref="DynamicMetaObject"/>.
            /// </param>
            /// <param name="staticBind">
            /// Should this MetaObject bind to <see langword="static"/> or instance methods and fields.
            /// </param>
            public MetaObject(Expression expression, object value, bool staticBind) : base(expression, BindingRestrictions.Empty, value)
            {
                isStatic = staticBind;
            }

            /// <summary>
            /// Performs the binding of the dynamic invoke member operation.
            /// </summary>
            /// <param name="binder">
            /// An instance of the <see cref="InvokeMemberBinder"/> that represents the details of the dynamic operation.
            /// </param>
            /// <param name="args">
            /// An array of <see cref="DynamicMetaObject"/> instances - arguments to the invoke member operation.
            /// </param>
            /// <returns>
            /// The new <see cref="DynamicMetaObject"/> representing the result of the binding.
            /// </returns>
            /// <exception cref="MissingMemberException">
            /// There is an attempt to dynamically access a class member that does not exist.
            /// </exception>
            public override DynamicMetaObject BindInvokeMember(InvokeMemberBinder binder, DynamicMetaObject[] args)
            {
                var self = Expression;
                var exposed = (Exposed)Value;

                var argTypes = new Type[args.Length];
                var argExps = new Expression[args.Length];

                for (int i = 0; i < args.Length; ++i)
                {
                    argTypes[i] = args[i].LimitType;
                    argExps[i] = args[i].Expression;
                }

                var type = exposed.SubjectType;
                var declaringType = type;
                MethodInfo method;
                do
                {
                    method = declaringType.GetMethod(binder.Name, GetBindingFlags(), null, argTypes, null);
                } while (method == null && (declaringType = declaringType.BaseType) != null);

                if (method == null)
                {
                    throw new MissingMemberException(type.FullName, binder.Name);
                }

                var @this = isStatic
                    ? null
                    : Expression.Convert(Expression.Field(Expression.Convert(self, typeof(Exposed)), "value"), type);

                var target = Expression.Call(@this, method, argExps);
                var restrictions = BindingRestrictions.GetTypeRestriction(self, typeof(Exposed));

                return new DynamicMetaObject(ConvertExpressionType(binder.ReturnType, target), restrictions);
            }

            /// <summary>
            /// Performs the binding of the dynamic get member operation.
            /// </summary>
            /// <param name="binder">
            /// An instance of the <see cref="GetMemberBinder"/> that represents the details of the dynamic operation.
            /// </param>
            /// <returns>
            /// The new <see cref="DynamicMetaObject"/> representing the result of the binding.
            /// </returns>
            public override DynamicMetaObject BindGetMember(GetMemberBinder binder)
            {
                var self = Expression;

                var memberExpression = GetMemberExpression(self, binder.Name);

                var target = Expression.Convert(memberExpression, binder.ReturnType);
                var restrictions = BindingRestrictions.GetTypeRestriction(self, typeof(Exposed));

                return new DynamicMetaObject(target, restrictions);
            }

            /// <summary>
            /// Performs the binding of the dynamic set member operation.
            /// </summary>
            /// <param name="binder">
            /// An instance of the <see cref="SetMemberBinder"/> that represents the details of the dynamic operation.
            /// </param>
            /// <param name="value">
            /// The <see cref="DynamicMetaObject"/> representing the value for the set member operation.
            /// </param>
            /// <returns>
            /// The new <see cref="DynamicMetaObject"/> representing the result of the binding.
            /// </returns>
            public override DynamicMetaObject BindSetMember(SetMemberBinder binder, DynamicMetaObject value)
            {
                var self = Expression;

                var memberExpression = GetMemberExpression(self, binder.Name);

                var target =
                    Expression.Convert(
                        Expression.Assign(memberExpression, Expression.Convert(value.Expression, memberExpression.Type)),
                        binder.ReturnType);
                var restrictions = BindingRestrictions.GetTypeRestriction(self, typeof(Exposed));

                return new DynamicMetaObject(target, restrictions);
            }

            /// <summary>
            /// Generates the <see cref="Expression"/> for accessing a member by name.
            /// </summary>
            /// <param name="self">
            /// The <see cref="Expression"/> for accessing the <see cref="Exposed"/> instance.
            /// </param>
            /// <param name="memberName">
            /// The member name.
            /// </param>
            /// <returns>
            /// <see cref="MemberExpression"/> for accessing a member by name.
            /// </returns>
            /// <exception cref="MissingMemberException">
            /// </exception>
            private MemberExpression GetMemberExpression(Expression self, string memberName)
            {
                MemberExpression memberExpression = null;
                var type = ((Exposed)Value).SubjectType;
                var @this = isStatic
                    ? null
                    : Expression.Convert(Expression.Field(Expression.Convert(self, typeof(Exposed)), "value"), type);
                var declaringType = type;

                do
                {
                    var property = declaringType.GetProperty(memberName, GetBindingFlags());
                    if (property != null)
                    {
                        memberExpression = Expression.Property(@this, property);
                    }
                    else
                    {
                        var field = declaringType.GetField(memberName, GetBindingFlags());
                        if (field != null)
                        {
                            memberExpression = Expression.Field(@this, field);
                        }
                    }
                } while (memberExpression == null && (declaringType = declaringType.BaseType) != null);

                if (memberExpression == null)
                {
                    throw new MissingMemberException(type.FullName, memberName);
                }

                return memberExpression;
            }

            /// <summary>
            /// Coerces the expression type into the expected return type.
            /// </summary>
            /// <param name="expectedType">Type expeted at the dispatch site.</param>
            /// <param name="target">Expression to coerce.</param>
            /// <remarks>Dynamic dispatch expects a <see langword="void"/> method to return <see langword="null"/>.</remarks>
            /// <returns>Target expression coerced to the required type.</returns>
            private static Expression ConvertExpressionType(Type expectedType, Expression target)
            {
                if (target.Type == expectedType)
                {
                    return target;
                }

                if (target.Type == typeof(void))
                {
                    return Expression.Block(target, Expression.Default(expectedType));
                }

                if (expectedType == typeof(void))
                {
                    return Expression.Block(target, Expression.Empty());
                }

                return Expression.Convert(target, expectedType);
            }

            /// <summary>
            /// Returns <see cref="BindingFlags"/> for member search.
            /// </summary>
            /// <returns>
            /// Static or instance flags depending on <see cref="isStatic"/>.
            /// </returns>
            private BindingFlags GetBindingFlags()
            {
                return isStatic
                    ? BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    : BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            }
        }
    }
}
