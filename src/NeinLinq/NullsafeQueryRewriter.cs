﻿using System;
using System.Collections.Generic;
using System.Linq.Expressions;

#if !NET40

using System.Reflection;

#endif

namespace NeinLinq
{
    /// <summary>
    /// Expression visitor for making member access null-safe.
    /// </summary>
    public class NullsafeQueryRewriter : ExpressionVisitor
    {
        static readonly ObjectCache<Type, Expression> cache = new ObjectCache<Type, Expression>();

        /// <inheritdoc />
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            var result = (MemberExpression)base.VisitMember(node);

            if (!IsSafe(result.Expression))
            {
                // insert null-check before accessing property or field
                return BeSafe(result, result.Expression, result.Update);
            }

            return result;
        }

        /// <inheritdoc />
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            var result = (MethodCallExpression)base.VisitMethodCall(node);

            if (!IsSafe(result.Object))
            {
                // insert null-check before invoking instance method
                return BeSafe(result, result.Object, fallback => result.Update(fallback, result.Arguments));
            }

            if (result.Method.IsExtensionMethod() && !IsSafe(result.Arguments[0]))
            {
                // insert null-check before invoking extension method
                return BeSafe(result, result.Arguments[0], fallback =>
                {
                    var arguments = new Expression[result.Arguments.Count];
                    result.Arguments.CopyTo(arguments, 0);
                    arguments[0] = fallback;

                    return result.Update(result.Object, arguments);
                });
            }

            return result;
        }

        static Expression BeSafe(Expression expression, Expression target, Func<Expression, Expression> update)
        {
            var fallback = cache.GetOrAdd(target.Type, Fallback);

            if (fallback != null)
            {
                // coalesce instead, a bit intrusive but fast...
                return update(Expression.Coalesce(target, fallback));
            }

            // target can be null, which is why we are actually here...
            var targetFallback = Expression.Constant(null, target.Type);

            // expression can be default or null, which is basically the same...
            var expressionFallback = !expression.Type.IsNullableOrReferenceType()
                ? (Expression)Expression.Default(expression.Type) : Expression.Constant(null, expression.Type);

            return Expression.Condition(Expression.Equal(target, targetFallback), expressionFallback, expression);
        }

        static bool IsSafe(Expression expression)
        {
            // in method call results and constant values we trust to avoid too much conditions...
            return expression == null
                || expression.NodeType == ExpressionType.Call
                || expression.NodeType == ExpressionType.Constant
                || !expression.Type.IsNullableOrReferenceType();
        }

        static Expression Fallback(Type type)
        {
            // default values for generic collections
            if (type.IsConstructedGenericType() && type.GenericTypeArguments().Length == 1)
            {
                return CollectionFallback(typeof(List<>), type)
                    ?? CollectionFallback(typeof(HashSet<>), type);
            }

            // default value for arrays
            if (type.IsArray)
            {
                return Expression.NewArrayInit(type.GetElementType());
            }

            return null;
        }

        static Expression CollectionFallback(Type definition, Type type)
        {
            var collection = definition.MakeGenericType(type.GenericTypeArguments());

            // try if an instance of this collection would suffice
            if (type.GetTypeInfo().IsAssignableFrom(collection.GetTypeInfo()))
            {
                return Expression.Convert(Expression.New(collection), type);
            }

            return null;
        }
    }
}
