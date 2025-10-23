namespace Chr.Avro.Serialization
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq.Expressions;
    using System.Reflection;
    using Chr.Avro.Abstract;

    /// <summary>
    /// Implements a <see cref="BinarySerializerBuilder" /> case that matches <see cref="ArraySchema" />
    /// and attempts to map it to enumerable types.
    /// </summary>
    public class BinaryArraySerializerBuilderCase : ArraySerializerBuilderCase, IBinarySerializerBuilderCase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BinaryArraySerializerBuilderCase" /> class.
        /// </summary>
        /// <param name="serializerBuilder">
        /// A serializer builder instance that will be used to build item serializers.
        /// </param>
        public BinaryArraySerializerBuilderCase(IBinarySerializerBuilder serializerBuilder)
        {
            SerializerBuilder = serializerBuilder ?? throw new ArgumentNullException(nameof(serializerBuilder), "Binary serializer builder cannot be null.");
        }

        /// <summary>
        /// Gets the serializer builder instance that will be used to build item serializers.
        /// </summary>
        public IBinarySerializerBuilder SerializerBuilder { get; }

        /// <summary>
        /// Builds a <see cref="BinarySerializer{T}" /> for an <see cref="ArraySchema" />.
        /// </summary>
        /// <returns>
        /// A successful <see cref="BinarySerializerBuilderCaseResult" /> if <paramref name="type" />
        /// is an enumerable type and <paramref name="schema" /> is an <see cref="ArraySchema" />;
        /// an unsuccessful <see cref="BinarySerializerBuilderCaseResult" /> otherwise.
        /// </returns>
        /// <exception cref="UnsupportedTypeException">
        /// Thrown when <paramref name="type" /> does not implement <see cref="IEnumerable{T}" />.
        /// </exception>
        /// <inheritdoc />
        public virtual BinarySerializerBuilderCaseResult BuildExpression(Expression value, Type type, Schema schema, BinarySerializerBuilderContext context)
        {
            if (schema is ArraySchema arraySchema)
            {
                var itemType = GetEnumerableType(type);

                if (itemType is not null || type == typeof(object))
                {
                    // support dynamic mapping:
                    itemType ??= typeof(object);
                    var readOnlyCollectionType = typeof(IReadOnlyCollection<>).MakeGenericType(itemType);
                    var enumerableType = typeof(IEnumerable<>).MakeGenericType(itemType);
                    var enumeratorType = typeof(IEnumerator<>).MakeGenericType(itemType);

                    Expression expression;
                    try
                    {
                        if (readOnlyCollectionType.IsAssignableFrom(type))
                        {
                            // NOTE: Not casting the expression to allow us to get the specific enumerator of `type`
                            // This way we can avoid the allocation of an IEnumerator<T> and the overhead of
                            // virtual dispatch calls
                            expression = value;
                        }
                        else
                        {
                            expression = BuildConversion(value, readOnlyCollectionType);
                        }
                    }
                    catch (Exception exception)
                    {
                        throw new UnsupportedTypeException(type, $"Failed to map {arraySchema} to {type}.", exception);
                    }

                    var collection = Expression.Variable(expression.Type);

                    var loop = Expression.Label();

                    // Try to get method and properties from the actual type, and when they are not found
                    // look in IReadOnlyCollection<T>.
                    // When type is an interface like IReadOnlyList<T>, the fallback will be used
                    var getCount = GetProperty(collection.Type, nameof(ICollection.Count), readOnlyCollectionType);
                    var getEnumerator = GetMethod(collection.Type, nameof(IEnumerable.GetEnumerator), Type.EmptyTypes, enumerableType);

                    // NOTE: IEnumerator<T> does not define an MoveNext method. It inherits from IEnumerator
                    // So we need to fallback to the base interface if the method is not found
                    var moveNext = GetMethod(getEnumerator.ReturnType, nameof(IEnumerator.MoveNext), Type.EmptyTypes, typeof(IEnumerator));

                    var enumerator = Expression.Variable(getEnumerator.ReturnType);

                    var getCurrent = enumerator.Type
                        .GetProperty(nameof(IEnumerator.Current))
                        ;

                    var writeInteger = typeof(BinaryWriter)
                        .GetMethod(nameof(BinaryWriter.WriteInteger), new[] { typeof(long) });

                    var writeItem = SerializerBuilder
                        .BuildExpression(Expression.Property(enumerator, getCurrent), arraySchema.Item, context);

                    var dispose = GetMethod(enumerator.Type, nameof(IDisposable.Dispose), Type.EmptyTypes, typeof(IDisposable));
                    Expression disposeCall = Expression.Empty();
                    if (dispose is not null)
                    {
                        // NOTE: Some Enumerator implementations don't have a Dispose method,
                        // like ImmutableArray<T>.Enumerator and ArrayEnumerator.
                        // We could remove the try/finally
                        disposeCall = Expression.Call(enumerator, dispose);
                    }

                    // if (collection.Count > 0)
                    // {
                    //     writer.WriteInteger((long)collection.Count);
                    //
                    //     var enumerator = collection.GetEnumerator();
                    //
                    //     try
                    //     {
                    //         // primitive foreach:
                    //         loop: while (true)
                    //         {
                    //             if (enumerator.MoveNext())
                    //             {
                    //                 ...
                    //             }
                    //             else
                    //             {
                    //                 break loop;
                    //             }
                    //         }
                    //     }
                    //     finally
                    //     {
                    //         enumerator.Dispose();
                    //     }
                    // }
                    //
                    // // write closing block:
                    // writer.WriteInteger(0L);
                    return BinarySerializerBuilderCaseResult.FromExpression(
                        Expression.Block(
                            new[] { collection, enumerator },
                            Expression.Assign(collection, expression),
                            Expression.IfThen(
                                Expression.GreaterThan(
                                    Expression.Property(collection, getCount),
                                    Expression.Constant(0)),
                                Expression.Block(
                                    Expression.Call(
                                        context.Writer,
                                        writeInteger,
                                        Expression.Convert(
                                            Expression.Property(collection, getCount),
                                            typeof(long))),
                                    Expression.Assign(
                                        enumerator,
                                        Expression.Call(collection, getEnumerator)),
                                    Expression.TryFinally(
                                        Expression.Loop(
                                            Expression.IfThenElse(
                                                Expression.Call(enumerator, moveNext),
                                                writeItem,
                                                Expression.Break(loop)),
                                            loop),
                                        disposeCall))),
                            Expression.Call(
                                context.Writer,
                                writeInteger,
                                Expression.Constant(0L))));
                }
                else
                {
                    return BinarySerializerBuilderCaseResult.FromException(new UnsupportedTypeException(type, $"{nameof(BinaryArraySerializerBuilderCase)} can only be applied to enumerable types."));
                }
            }
            else
            {
                return BinarySerializerBuilderCaseResult.FromException(new UnsupportedSchemaException(schema, $"{nameof(BinaryArraySerializerBuilderCase)} can only be applied to {nameof(ArraySchema)}s."));
            }
        }

        private static PropertyInfo GetProperty(Type type, string name, Type fallback)
        {
            var property = type.GetProperty(name);
            if (property is not null)
            {
                return property;
            }

            if (fallback.IsAssignableFrom(type))
            {
                property = fallback.GetProperty(name);
            }
            return property;
        }

        private static MethodInfo GetMethod(Type type, string name, Type[] args, Type fallback)
        {
            var method = type.GetMethod(name, args);
            if (method is not null)
            {
                return method;
            }

            if (fallback.IsAssignableFrom(type))
            {
                method = fallback.GetMethod(name, args);
            }
            return method;
        }
    }
}
