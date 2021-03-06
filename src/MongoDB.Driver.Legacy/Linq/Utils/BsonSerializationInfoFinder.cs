/* Copyright 2010-2016 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson.Serialization;

namespace MongoDB.Driver.Linq.Utils
{
    /// <summary>
    /// Used to find the BsonSerializationInfo for a given expression representing accessing a document element.
    /// </summary>
    internal class BsonSerializationInfoFinder : ExpressionVisitor<BsonSerializationInfo>
    {
        // private fields
        private Dictionary<Expression, BsonSerializationInfo> _serializationInfoCache;

        // constructors
        /// <summary>
        /// Initializes a new instance of the BsonSerializationInfoFinder class.
        /// </summary>
        private BsonSerializationInfoFinder(Dictionary<Expression, BsonSerializationInfo> serializationInfoCache)
        {
            _serializationInfoCache = serializationInfoCache ?? new Dictionary<Expression, BsonSerializationInfo>();
        }

        // public static methods
        /// <summary>
        /// Gets the serialization info for the node utilizing precalculated serialization information.
        /// </summary>
        /// <param name="node">The expression.</param>
        /// <param name="serializationInfoCache">The serialization info cache.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        public static BsonSerializationInfo GetSerializationInfo(Expression node, Dictionary<Expression, BsonSerializationInfo> serializationInfoCache)
        {
            BsonSerializationInfo serializationInfo;
            if (!TryGetSerializationInfo(node, serializationInfoCache, out serializationInfo))
            {
                string message = string.Format("Unable to determine the serialization information for the expression: {0}.",
                    ExpressionFormatter.ToString(node));
                throw new NotSupportedException(message);
            }

            return serializationInfo;
        }

        /// <summary>
        /// Tries the get serialization information.
        /// </summary>
        /// <param name="node">The node.</param>
        /// <param name="serializationInfoCache">The serialization information cache.</param>
        /// <param name="serializationInfo">The serialization information.</param>
        /// <returns></returns>
        public static bool TryGetSerializationInfo(Expression node, Dictionary<Expression, BsonSerializationInfo> serializationInfoCache, out BsonSerializationInfo serializationInfo)
        {
            var finder = new BsonSerializationInfoFinder(serializationInfoCache);
            serializationInfo = finder.Visit(node);
            return serializationInfo != null;
        }

        // protected methods
        /// <summary>
        /// Visits an Expression.
        /// </summary>
        /// <param name="node">The Expression.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        protected override BsonSerializationInfo Visit(Expression node)
        {
            BsonSerializationInfo serializationInfo;
            if (_serializationInfoCache.TryGetValue(node, out serializationInfo))
            {
                return serializationInfo;
            }

            return base.Visit(node);
        }

        // protected methods
        /// <summary>
        /// Visits a BinaryExpression.
        /// </summary>
        /// <param name="node">The BinaryExpression.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        protected override BsonSerializationInfo VisitBinary(BinaryExpression node)
        {
            if (node.NodeType != ExpressionType.ArrayIndex)
            {
                return null;
            }

            var serializationInfo = Visit(node.Left);
            if (serializationInfo == null)
            {
                return null;
            }

            var indexEpression = node.Right as ConstantExpression;
            if (indexEpression == null)
            {
                return null;
            }
            var index = Convert.ToInt32(indexEpression.Value);

            var arraySerializer = serializationInfo.Serializer as IBsonArraySerializer;
            BsonSerializationInfo itemSerializationInfo;
            if (arraySerializer == null || !arraySerializer.TryGetItemSerializationInfo(out itemSerializationInfo))
            {
                return null;
            }

            itemSerializationInfo = new BsonSerializationInfo(
                index.ToString(),
                itemSerializationInfo.Serializer,
                itemSerializationInfo.NominalType);

            return CombineSerializationInfo(serializationInfo, itemSerializationInfo);
        }

        /// <summary>
        /// Visits a LambdaExpression.
        /// </summary>
        /// <param name="node">The LambdaExpression.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        protected override BsonSerializationInfo VisitLambda(LambdaExpression node)
        {
            return Visit(node.Body);
        }

        /// <summary>
        /// Visits a MemberExpression.
        /// </summary>
        /// <param name="node">The MemberExpression.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        protected override BsonSerializationInfo VisitMember(MemberExpression node)
        {
            var serializationInfo = Visit(node.Expression);
            if (serializationInfo == null)
            {
                return null;
            }

            var documentSerializer = serializationInfo.Serializer as IBsonDocumentSerializer;
            if (documentSerializer == null)
            {
                return null;
            }

            BsonSerializationInfo memberSerializationInfo;
            if (!documentSerializer.TryGetMemberSerializationInfo(node.Member.Name, out memberSerializationInfo))
            {
                var message = string.Format("The member {0} does not exist.", node.Member.Name);
                throw new ArgumentOutOfRangeException("memberName", message);
            }
            return CombineSerializationInfo(serializationInfo, memberSerializationInfo);
        }

        /// <summary>
        /// Visits a MethodCallExpression.
        /// </summary>
        /// <param name="node">The MethodCallExpression.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        protected override BsonSerializationInfo VisitMethodCall(MethodCallExpression node)
        {
            switch (node.Method.Name)
            {
                case "ElementAt":
                    return VisitElementAt(node);
                case "get_Item":
                    return VisitGetItem(node);
            }

            return null;
        }

        /// <summary>
        /// Visits a ParameterExpression.
        /// </summary>
        /// <param name="node">The ParameterExpression.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        protected override BsonSerializationInfo VisitParameter(ParameterExpression node)
        {
            var serializer = BsonSerializer.LookupSerializer(node.Type);
            var serializationInfo = CreateSerializationInfo(node, serializer);
            _serializationInfoCache.Add(node, serializationInfo);
            return serializationInfo;
        }

        /// <summary>
        /// Visits a UnaryExpression.
        /// </summary>
        /// <param name="node">The UnaryExpression.</param>
        /// <returns>BsonSerializationInfo for the expression.</returns>
        protected override BsonSerializationInfo VisitUnary(UnaryExpression node)
        {
            if (node.NodeType != ExpressionType.Convert && node.NodeType != ExpressionType.ConvertChecked)
            {
                return null;
            }

            var serializationInfo = Visit(node.Operand);
            if (serializationInfo == null)
            {
                return null;
            }

            // if the target conversion type cannot be assigned from the operand, than we are downcasting and we need to get the more specific serializer
            if (!node.Type.GetTypeInfo().IsAssignableFrom(node.Operand.Type))
            {
                var conversionSerializer = BsonSerializer.LookupSerializer(node.Type);
                var conversionSerializationInfo = CreateSerializationInfo(node, conversionSerializer);
                return CombineSerializationInfo(serializationInfo, conversionSerializationInfo);
            }

            return serializationInfo;
        }

        // private methods
        private BsonSerializationInfo VisitGetItem(MethodCallExpression node)
        {
            var arguments = node.Arguments.ToArray();
            if (arguments.Length != 1)
            {
                return null;
            }

            var indexExpression = arguments[0] as ConstantExpression;
            if (indexExpression == null)
            {
                return null;
            }

            var serializationInfo = Visit(node.Object);
            if (serializationInfo == null)
            {
                return null;
            }

            var indexName = indexExpression.Value.ToString();
            if (indexExpression.Type == typeof(int) ||
                indexExpression.Type == typeof(uint) ||
                indexExpression.Type == typeof(long) ||
                indexExpression.Type == typeof(ulong))
            {
                var arraySerializer = serializationInfo.Serializer as IBsonArraySerializer;
                BsonSerializationInfo itemSerializationInfo;
                if (arraySerializer != null && arraySerializer.TryGetItemSerializationInfo(out itemSerializationInfo))
                {
                    itemSerializationInfo = new BsonSerializationInfo(
                        indexName,
                        itemSerializationInfo.Serializer,
                        itemSerializationInfo.NominalType);

                    return CombineSerializationInfo(serializationInfo, itemSerializationInfo);
                }
            }

            var documentSerializer = serializationInfo.Serializer as IBsonDocumentSerializer;
            BsonSerializationInfo memberSerializationInfo;
            if (documentSerializer != null && documentSerializer.TryGetMemberSerializationInfo(indexName, out memberSerializationInfo))
            {
                return CombineSerializationInfo(serializationInfo, memberSerializationInfo);
            }

            return null;
        }

        private BsonSerializationInfo VisitElementAt(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(Enumerable) && node.Method.DeclaringType != typeof(Queryable))
            {
                return null;
            }

            var serializationInfo = Visit(node.Arguments[0]);
            if (serializationInfo == null)
            {
                return null;
            }

            var arraySerializer = serializationInfo.Serializer as IBsonArraySerializer;
            BsonSerializationInfo itemSerializationInfo;
            if (arraySerializer == null || !arraySerializer.TryGetItemSerializationInfo(out itemSerializationInfo))
            {
                return null;
            }

            var index = (int)((ConstantExpression)node.Arguments[1]).Value;
            itemSerializationInfo = new BsonSerializationInfo(
                index.ToString(),
                itemSerializationInfo.Serializer,
                itemSerializationInfo.NominalType);

            return CombineSerializationInfo(serializationInfo, itemSerializationInfo);
        }

        private static BsonSerializationInfo CombineSerializationInfo(BsonSerializationInfo baseInfo, BsonSerializationInfo newInfo)
        {
            string elementName = null;
            if (baseInfo.ElementName != null && newInfo.ElementName != null)
            {
                elementName = baseInfo.ElementName + "." + newInfo.ElementName;
            }
            else if (baseInfo.ElementName != null)
            {
                elementName = baseInfo.ElementName;
            }
            else if (newInfo.ElementName != null)
            {
                elementName = newInfo.ElementName;
            }

            return new BsonSerializationInfo(
                elementName,
                newInfo.Serializer,
                newInfo.NominalType);
        }

        private static BsonSerializationInfo CreateSerializationInfo(Expression node, IBsonSerializer serializer)
        {
            return new BsonSerializationInfo(
                null,
                serializer,
                node.Type);
        }
    }
}
