using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Pine.PineVM;

public class PineVM
{
    public delegate Result<string, PineValue> EvalExprDelegate(Expression expression, PineValue environment);

    public record FunctionApplicationCacheEntryKey(PineValue function, PineValue argument);

    readonly ConcurrentDictionary<FunctionApplicationCacheEntryKey, PineValue> functionApplicationCache = new();

    public IImmutableDictionary<FunctionApplicationCacheEntryKey, PineValue> CopyFunctionApplicationCache() =>
        functionApplicationCache.ToImmutableDictionary();

    public long FunctionApplicationCacheSize => functionApplicationCache.Count;

    public long FunctionApplicationCacheLookupCount { private set; get; }

    public long FunctionApplicationMaxEnvSize { private set; get; }

    readonly IReadOnlyDictionary<PineValue, Expression.DelegatingExpression>? decodeExpressionOverrides;

    readonly EvalExprDelegate evalExprDelegate;

    public PineVM(
        IReadOnlyDictionary<PineValue, Func<EvalExprDelegate, PineValue, Result<string, PineValue>>>? decodeExpressionOverrides = null,
        Func<EvalExprDelegate, EvalExprDelegate>? overrideEvaluateExpression = null)
    {
        evalExprDelegate =
            overrideEvaluateExpression?.Invoke(EvaluateExpressionDefault) ?? EvaluateExpressionDefault;

        this.decodeExpressionOverrides =
            (decodeExpressionOverrides ?? PineVMConfiguration.DecodeExpressionOverrides)
            ?.ToImmutableDictionary(
                keySelector: encodedExprAndDelegate => encodedExprAndDelegate.Key,
                elementSelector: encodedExprAndDelegate => new Expression.DelegatingExpression(encodedExprAndDelegate.Value));
    }

    public Result<string, PineValue> EvaluateExpression(
        Expression expression,
        PineValue environment) => evalExprDelegate(expression, environment);

    public Result<string, PineValue> EvaluateExpressionDefault(
        Expression expression,
        PineValue environment)
    {
        if (expression is Expression.LiteralExpression literalExpression)
            return Result<string, PineValue>.ok(literalExpression.Value);

        if (expression is Expression.ListExpression listExpression)
        {
            return
                ResultListMapCombine(
                    listExpression.List,
                    elem => evalExprDelegate(elem, environment))
                .MapError(err => "Failed to evaluate list element: " + err)
                .Map(PineValue.List);
        }

        if (expression is Expression.DecodeAndEvaluateExpression applicationExpression)
        {
            return
                EvaluateDecodeAndEvaluateExpression(applicationExpression, environment)
                .MapError(err => "Failed to evaluate decode and evaluate: " + err);
        }

        if (expression is Expression.KernelApplicationExpression kernelApplicationExpression)
        {
            return
                EvaluateKernelApplicationExpression(environment, kernelApplicationExpression)
                .MapError(err => "Failed to evaluate kernel function application: " + err);
        }

        if (expression is Expression.ConditionalExpression conditionalExpression)
        {
            return EvaluateConditionalExpression(environment, conditionalExpression);
        }

        if (expression is Expression.EnvironmentExpression)
        {
            return Result<string, PineValue>.ok(environment);
        }

        if (expression is Expression.StringTagExpression stringTagExpression)
        {
            return EvaluateExpression(stringTagExpression.tagged, environment);
        }

        if (expression is Expression.DelegatingExpression delegatingExpr)
        {
            return delegatingExpr.Delegate.Invoke(evalExprDelegate, environment);
        }

        throw new NotImplementedException("Unexpected shape of expression: " + expression.GetType().FullName);
    }

    public Result<string, PineValue> EvaluateDecodeAndEvaluateExpression(
        Expression.DecodeAndEvaluateExpression decodeAndEvaluate,
        PineValue environment) =>
        EvaluateExpression(decodeAndEvaluate.expression, environment)
        .MapError(error => "Failed to evaluate function: " + error)
        .AndThen(functionValue => DecodeExpressionFromValue(functionValue)
        .MapError(error => "Failed to decode expression from function value: " + error)
        .AndThen(functionExpression => EvaluateExpression(decodeAndEvaluate.environment, environment)
        .MapError(error => "Failed to evaluate argument: " + error)
        .AndThen(argumentValue =>
        {
            ++FunctionApplicationCacheLookupCount;

            var cacheKey = new FunctionApplicationCacheEntryKey(function: functionValue, argument: argumentValue);

            if (functionApplicationCache.TryGetValue(cacheKey, out var cachedResult))
                return Result<string, PineValue>.ok(cachedResult);

            if (argumentValue is PineValue.ListValue list)
            {
                FunctionApplicationMaxEnvSize =
                FunctionApplicationMaxEnvSize < list.Elements.Count ? list.Elements.Count : FunctionApplicationMaxEnvSize;
            }

            var evalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var evalResult = EvaluateExpression(environment: argumentValue, expression: functionExpression);

            if (4 <= evalStopwatch.ElapsedMilliseconds && evalResult is Result<string, PineValue>.Ok evalOk)
            {
                functionApplicationCache[cacheKey] = evalOk.Value;
            }

            return evalResult;
        })));

    public Result<string, PineValue> EvaluateKernelApplicationExpression(
        PineValue environment,
        Expression.KernelApplicationExpression application) =>
        EvaluateExpression(application.argument, environment)
        .MapError(error => "Failed to evaluate argument: " + error)
        .Map(argument => application.function(argument).WithDefault(PineValue.EmptyList));

    public Result<string, PineValue> EvaluateConditionalExpression(
        PineValue environment,
        Expression.ConditionalExpression conditional) =>
        EvaluateExpression(conditional.condition, environment)
        .MapError(error => "Failed to evaluate condition: " + error)
        .AndThen(conditionValue =>
        EvaluateExpression(conditionValue == TrueValue ? conditional.ifTrue : conditional.ifFalse, environment));

    static readonly IReadOnlyDictionary<string, Func<PineValue, Result<string, PineValue>>> NamedKernelFunctions =
        ImmutableDictionary<string, Func<PineValue, Result<string, PineValue>>>.Empty
        .SetItem(nameof(KernelFunction.equal), KernelFunction.equal)
        .SetItem(nameof(KernelFunction.logical_not), KernelFunction.logical_not)
        .SetItem(nameof(KernelFunction.logical_and), KernelFunction.logical_and)
        .SetItem(nameof(KernelFunction.logical_or), KernelFunction.logical_or)
        .SetItem(nameof(KernelFunction.length), KernelFunction.length)
        .SetItem(nameof(KernelFunction.skip), KernelFunction.skip)
        .SetItem(nameof(KernelFunction.take), KernelFunction.take)
        .SetItem(nameof(KernelFunction.reverse), KernelFunction.reverse)
        .SetItem(nameof(KernelFunction.concat), KernelFunction.concat)
        .SetItem(nameof(KernelFunction.list_head), KernelFunction.list_head)
        .SetItem(nameof(KernelFunction.neg_int), KernelFunction.neg_int)
        .SetItem(nameof(KernelFunction.add_int), KernelFunction.add_int)
        .SetItem(nameof(KernelFunction.sub_int), KernelFunction.sub_int)
        .SetItem(nameof(KernelFunction.mul_int), KernelFunction.mul_int)
        .SetItem(nameof(KernelFunction.div_int), KernelFunction.div_int)
        .SetItem(nameof(KernelFunction.is_sorted_ascending_int), KernelFunction.is_sorted_ascending_int);

    static public PineValue ValueFromBool(bool b) => b ? TrueValue : FalseValue;

    static readonly public PineValue TrueValue = PineValue.Blob(new byte[] { 4 });

    static readonly public PineValue FalseValue = PineValue.Blob(new byte[] { 2 });

    static public Result<string, bool> DecodeBoolFromValue(PineValue value) =>
        value == TrueValue
        ?
        Result<string, bool>.ok(true)
        :
        value == FalseValue ? Result<string, bool>.ok(false)
        :
        Result<string, bool>.err("Value is neither True nor False");

    static public Result<string, PineValue> EncodeExpressionAsValue(Expression expression) =>
        expression switch
        {
            Expression.LiteralExpression literal =>
            Result<string, PineValue>.ok(EncodeChoiceTypeVariantAsPineValue("Literal", literal.Value)),

            Expression.EnvironmentExpression =>
            Result<string, PineValue>.ok(EncodeChoiceTypeVariantAsPineValue("Environment", PineValue.EmptyList)),

            Expression.ListExpression list =>
            list.List.Select(EncodeExpressionAsValue)
            .ListCombine()
            .Map(listElements => EncodeChoiceTypeVariantAsPineValue("List", PineValue.List(listElements))),

            Expression.ConditionalExpression conditional =>
            EncodeConditionalExpressionAsValue(conditional),

            Expression.DecodeAndEvaluateExpression decodeAndEval =>
            EncodeDecodeAndEvaluateExpression(decodeAndEval),

            Expression.KernelApplicationExpression kernelAppl =>
            EncodeKernelApplicationExpression(kernelAppl),

            Expression.StringTagExpression stringTag =>
            EncodeExpressionAsValue(stringTag.tagged)
            .Map(encodedTagged => EncodeChoiceTypeVariantAsPineValue(
                "StringTag",
                PineValue.List(new[] { PineValueAsString.ValueFromString(stringTag.tag), encodedTagged }))),

            _ =>
            Result<string, PineValue>.err("Unsupported expression type: " + expression.GetType().FullName)
        };

    public Result<string, Expression> DecodeExpressionFromValue(PineValue value)
    {
        if (decodeExpressionOverrides?.TryGetValue(value, out var delegatingExpression) ?? false)
            return Result<string, Expression>.ok(delegatingExpression);

        return
            DecodeChoiceFromPineValue(
                generalDecoder: DecodeExpressionFromValue,
                ExpressionDecoders,
                value);
    }

    static public Result<string, Expression> DecodeExpressionFromValueDefault(PineValue value) =>
        DecodeChoiceFromPineValue(
            generalDecoder: DecodeExpressionFromValueDefault,
            ExpressionDecoders,
            value);

    static readonly IImmutableDictionary<string, Func<Func<PineValue, Result<string, Expression>>, PineValue, Result<string, Expression>>> ExpressionDecoders =
        ImmutableDictionary<string, Func<Func<PineValue, Result<string, Expression>>, PineValue, Result<string, Expression>>>.Empty
        .SetItem(
            "Literal",
            (generalDecoder, literal) => Result<string, Expression>.ok(new Expression.LiteralExpression(literal)))
        .SetItem(
            "List",
            (generalDecoder, listValue) =>
            DecodePineListValue(listValue)
            .AndThen(list => ResultListMapCombine(list, generalDecoder))
            .Map(expressionList => (Expression)new Expression.ListExpression(expressionList.ToImmutableArray())))
        .SetItem(
            "DecodeAndEvaluate",
            (generalDecoder, value) => DecodeDecodeAndEvaluateExpression(generalDecoder, value)
            .Map(application => (Expression)application))
        .SetItem(
            "KernelApplication",
            (generalDecoder, value) => DecodeKernelApplicationExpression(generalDecoder, value)
            .Map(application => (Expression)application))
        .SetItem(
            "Conditional",
            (generalDecoder, value) => DecodeConditionalExpression(generalDecoder, value)
            .Map(conditional => (Expression)conditional))
        .SetItem(
            "Environment",
            (_, _) => Result<string, Expression>.ok(new Expression.EnvironmentExpression()))
        .SetItem(
            "StringTag",
            (generalDecoder, value) => DecodeStringTagExpression(generalDecoder, value)
            .Map(stringTag => (Expression)stringTag));

    static public Result<string, PineValue> EncodeDecodeAndEvaluateExpression(Expression.DecodeAndEvaluateExpression decodeAndEval) =>
        EncodeExpressionAsValue(decodeAndEval.expression)
        .AndThen(encodedExpression =>
        EncodeExpressionAsValue(decodeAndEval.environment)
        .Map(encodedEnvironment =>
        EncodeChoiceTypeVariantAsPineValue("DecodeAndEvaluate",
            EncodeRecordToPineValue(
                (nameof(Expression.DecodeAndEvaluateExpression.expression), encodedExpression),
                (nameof(Expression.DecodeAndEvaluateExpression.environment), encodedEnvironment)))));

    static public Result<string, Expression.DecodeAndEvaluateExpression> DecodeDecodeAndEvaluateExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodeRecord2FromPineValue(
            value,
            ("expression", generalDecoder),
            ("environment", generalDecoder),
            (expression, environment) => new Expression.DecodeAndEvaluateExpression(expression: expression, environment: environment));

    static public Result<string, PineValue> EncodeKernelApplicationExpression(Expression.KernelApplicationExpression kernelApplicationExpression) =>
        EncodeExpressionAsValue(kernelApplicationExpression.argument)
        .Map(encodedArgument =>
        EncodeChoiceTypeVariantAsPineValue("KernelApplication",
            EncodeRecordToPineValue(
                (nameof(Expression.KernelApplicationExpression.functionName), PineValueAsString.ValueFromString(kernelApplicationExpression.functionName)),
                (nameof(Expression.KernelApplicationExpression.argument), encodedArgument))));

    static public Result<string, Expression.KernelApplicationExpression> DecodeKernelApplicationExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodeRecord2FromPineValue(
            value,
            (nameof(Expression.KernelApplicationExpression.functionName), StringFromComponent: PineValueAsString.StringFromValue),
            (nameof(Expression.KernelApplicationExpression.argument), generalDecoder),
            (functionName, argument) => (functionName, argument))
        .AndThen(functionNameAndArgument =>
        DecodeKernelApplicationExpression(functionNameAndArgument.functionName, functionNameAndArgument.argument));

    static public Expression.KernelApplicationExpression DecodeKernelApplicationExpressionThrowOnUnknownName(
        string functionName,
        Expression argument) =>
        DecodeKernelApplicationExpression(functionName, argument)
        .Extract(err => throw new Exception(err));

    static public Result<string, Expression.KernelApplicationExpression> DecodeKernelApplicationExpression(
        string functionName,
        Expression argument)
    {
        if (!NamedKernelFunctions.TryGetValue(functionName, out var kernelFunction))
        {
            return Result<string, Expression.KernelApplicationExpression>.err(
                "Did not find kernel function '" + functionName + "'");
        }

        return Result<string, Expression.KernelApplicationExpression>.ok(
            new Expression.KernelApplicationExpression(
                functionName: functionName,
                function: kernelFunction,
                argument: argument));
    }

    static public Result<string, PineValue> EncodeConditionalExpressionAsValue(Expression.ConditionalExpression conditionalExpression) =>
        EncodeExpressionAsValue(conditionalExpression.condition)
        .AndThen(encodedCondition =>
        EncodeExpressionAsValue(conditionalExpression.ifTrue)
        .AndThen(encodedIfTrue =>
        EncodeExpressionAsValue(conditionalExpression.ifFalse)
        .Map(encodedIfFalse =>
        EncodeChoiceTypeVariantAsPineValue("Conditional",
            EncodeRecordToPineValue(
                (nameof(Expression.ConditionalExpression.condition), encodedCondition),
                (nameof(Expression.ConditionalExpression.ifTrue), encodedIfTrue),
                (nameof(Expression.ConditionalExpression.ifFalse), encodedIfFalse))))));

    static public Result<string, Expression.ConditionalExpression> DecodeConditionalExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodeRecord3FromPineValue(
            value,
            (nameof(Expression.ConditionalExpression.condition), generalDecoder),
            (nameof(Expression.ConditionalExpression.ifTrue), generalDecoder),
            (nameof(Expression.ConditionalExpression.ifFalse), generalDecoder),
            (condition, ifTrue, ifFalse) => new Expression.ConditionalExpression(condition: condition, ifTrue: ifTrue, ifFalse: ifFalse));

    static public Result<string, Expression.StringTagExpression> DecodeStringTagExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodePineListValue(value)
        .AndThen(DecodeListWithExactlyTwoElements)
        .AndThen(tagValueAndTaggedValue =>
            PineValueAsString.StringFromValue(tagValueAndTaggedValue.Item1).MapError(err => "Failed to decode tag: " + err)
        .AndThen(tag => generalDecoder(tagValueAndTaggedValue.Item2).MapError(err => "Failed to decoded tagged expression: " + err)
        .Map(tagged => new Expression.StringTagExpression(tag: tag, tagged: tagged))));

    static public Result<ErrT, IReadOnlyList<MappedOkT>> ResultListMapCombine<ErrT, OkT, MappedOkT>(
        IReadOnlyList<OkT> list,
        Func<OkT, Result<ErrT, MappedOkT>> mapElement) =>
        list.Select(mapElement).ListCombine();

    static public Result<string, Record> DecodeRecord3FromPineValue<FieldA, FieldB, FieldC, Record>(
        PineValue value,
        (string name, Func<PineValue, Result<string, FieldA>> decode) fieldA,
        (string name, Func<PineValue, Result<string, FieldB>> decode) fieldB,
        (string name, Func<PineValue, Result<string, FieldC>> decode) fieldC,
        Func<FieldA, FieldB, FieldC, Record> compose) =>
        DecodeRecordFromPineValue(value)
        .AndThen(record =>
        {
            if (!record.TryGetValue(fieldA.name, out var fieldAValue))
                return Result<string, Record>.err("Did not find field " + fieldA.name);

            if (!record.TryGetValue(fieldB.name, out var fieldBValue))
                return Result<string, Record>.err("Did not find field " + fieldB.name);

            if (!record.TryGetValue(fieldC.name, out var fieldCValue))
                return Result<string, Record>.err("Did not find field " + fieldC.name);

            return
            fieldA.decode(fieldAValue)
            .AndThen(fieldADecoded =>
            fieldB.decode(fieldBValue)
            .AndThen(fieldBDecoded =>
            fieldC.decode(fieldCValue)
            .Map(fieldCDecoded => compose(fieldADecoded, fieldBDecoded, fieldCDecoded))));
        });

    static public Result<string, Record> DecodeRecord2FromPineValue<FieldA, FieldB, Record>(
        PineValue value,
        (string name, Func<PineValue, Result<string, FieldA>> decode) fieldA,
        (string name, Func<PineValue, Result<string, FieldB>> decode) fieldB,
        Func<FieldA, FieldB, Record> compose) =>
        DecodeRecordFromPineValue(value)
        .AndThen(record =>
        {
            if (!record.TryGetValue(fieldA.name, out var fieldAValue))
                return Result<string, Record>.err("Did not find field " + fieldA.name);

            if (!record.TryGetValue(fieldB.name, out var fieldBValue))
                return Result<string, Record>.err("Did not find field " + fieldB.name);

            return
            fieldA.decode(fieldAValue)
            .AndThen(fieldADecoded =>
            fieldB.decode(fieldBValue)
            .Map(fieldBDecoded => compose(fieldADecoded, fieldBDecoded)));
        });

    static public PineValue EncodeRecordToPineValue(params (string fieldName, PineValue fieldValue)[] fields) =>
        PineValue.List(
            fields.Select(field => PineValue.List(
                new[]
                {
                    PineValueAsString.ValueFromString(field.fieldName),
                    field.fieldValue
                })).ToArray());


    static public Result<string, ImmutableDictionary<string, PineValue>> DecodeRecordFromPineValue(PineValue value) =>
        DecodePineListValue(value)
        .AndThen(list =>
        list
        .Aggregate(
            seed: Result<string, ImmutableDictionary<string, PineValue>>.ok(ImmutableDictionary<string, PineValue>.Empty),
            func: (aggregate, listElement) => aggregate.AndThen(recordFields =>
            DecodePineListValue(listElement)
            .AndThen(DecodeListWithExactlyTwoElements)
            .AndThen(fieldNameValueAndValue => PineValueAsString.StringFromValue(fieldNameValueAndValue.Item1)
            .Map(fieldName => recordFields.SetItem(fieldName, fieldNameValueAndValue.Item2))))));

    static public PineValue EncodeChoiceTypeVariantAsPineValue(string tagName, PineValue tagArguments) =>
        PineValue.List(
            new[]
            {
                PineValueAsString.ValueFromString(tagName),
                tagArguments,
            });

    static public Result<string, T> DecodeChoiceFromPineValue<T>(
        Func<PineValue, Result<string, T>> generalDecoder,
        IImmutableDictionary<string, Func<Func<PineValue, Result<string, T>>, PineValue, Result<string, T>>> variants,
        PineValue value) =>
        DecodePineListValue(value)
        .AndThen(DecodeListWithExactlyTwoElements)
        .AndThen(tagNameValueAndValue => PineValueAsString.StringFromValue(tagNameValueAndValue.Item1)
        .MapError(error => "Failed to decode union tag name: " + error)
        .AndThen(tagName =>
        {
            if (!variants.TryGetValue(tagName, out var variant))
                return Result<string, T>.err("Unexpected tag name: " + tagName);

            return variant(generalDecoder, tagNameValueAndValue.Item2)!;
        }));

    static public Result<string, IImmutableList<PineValue>> DecodePineListValue(PineValue value)
    {
        if (value is not PineValue.ListValue listValue)
            return Result<string, IImmutableList<PineValue>>.err("Not a list");

        return Result<string, IImmutableList<PineValue>>.ok(
            listValue.Elements as IImmutableList<PineValue> ?? listValue.Elements.ToImmutableList());
    }

    static public Result<string, (T, T)> DecodeListWithExactlyTwoElements<T>(IImmutableList<T> list)
    {
        if (list.Count != 2)
            return Result<string, (T, T)>.err("Unexpected number of elements in list: Not 2 but " + list.Count);

        return Result<string, (T, T)>.ok((list[0], list[1]));
    }
}
