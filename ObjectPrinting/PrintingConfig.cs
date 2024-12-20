﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace ObjectPrinting;

public class PrintingConfig<TOwner>
{
    internal readonly Dictionary<Type, Func<object, string>> TypePrinters = new();
    internal readonly Dictionary<string, Func<object, string>> PropertyPrinters = new();
    internal IFormatProvider Culture = CultureInfo.InvariantCulture;
    internal readonly Dictionary<string, int> MaxLengths = new();
    private readonly HashSet<Type> excludedTypes = [];
    private readonly HashSet<string> excludedProperties = [];

    private readonly HashSet<Type> finalTypes =
    [
        typeof(int), typeof(double), typeof(float), typeof(DateTime), typeof(TimeSpan), typeof(string), typeof(bool),
        typeof(long)
    ];

    public ITypePrintingConfig<TOwner, TType> Printing<TType>() =>
        new TypePrintingConfig<TOwner, TType>(this);

    public IPropertyPrintingConfig<TOwner, TPropType> Printing<TPropType>(
        Expression<Func<TOwner, TPropType>> memberSelector)
    {
        var propertyName = GetPropertyName(memberSelector);
        return new PropertyPrintingConfig<TOwner, TPropType>(this, propertyName);
    }

    public PrintingConfig<TOwner> Excluding<TPropType>(Expression<Func<TOwner, TPropType>> memberSelector)
    {
        var propertyName = GetPropertyName(memberSelector);
        excludedProperties.Add(propertyName);
        return this;
    }

    public PrintingConfig<TOwner> Excluding<TPropType>()
    {
        excludedTypes.Add(typeof(TPropType));
        return this;
    }

    public string PrintToString(TOwner obj) => PrintToString(obj, 0);

    private static string GetPropertyName<TPropType>(Expression<Func<TOwner, TPropType>> memberSelector)
    {
        if (memberSelector.Body is not MemberExpression member)
            throw new ArgumentException("Expression must be a property access.");

        return member.Member.Name;
    }

    private string PrintToString(object? obj, int nestingLevel, Stack<string>? pathStack = null)
    {
        if (obj == null) return "null";
        var identation = new string('\t', nestingLevel);

        var type = obj.GetType();
        pathStack ??= new Stack<string>();
        if (IsCycleDependency(obj, type, pathStack))
            return identation + $"Cycle dependency: {obj}";
        pathStack.Push(type.FullName + obj.GetHashCode());
        if (finalTypes.Contains(obj.GetType())) return obj.ToString() ?? string.Empty;

        var sb = new StringBuilder();
        sb.Append(identation + type.Name);
        if (obj is not string && type.IsAssignableTo(typeof(IEnumerable)))
            return PrintEnumerable((IEnumerable)obj, nestingLevel, pathStack);

        sb.AppendLine(PrintMembersToString(obj.GetType().GetFields(),
            field => ((FieldInfo)field).GetValue(obj), nestingLevel, pathStack));
        sb.Append(PrintMembersToString(obj.GetType().GetProperties(),
            prop => ((PropertyInfo)prop).GetValue(obj), nestingLevel, pathStack));
        pathStack.Pop();
        return sb.ToString();
    }

    private bool IsCycleDependency(object? obj, Type type, Stack<string> pathStack)
        => type != typeof(string) &&
           !finalTypes.Contains(type) &&
           pathStack.Contains(type.FullName + obj?.GetHashCode());

    private string PrintMembersToString(IEnumerable<MemberInfo> members, Func<MemberInfo, object?> getValue,
        int nestingLevel, Stack<string> pathStack)
    {
        var identation = new string('\t', nestingLevel + 1);
        var items = (from memberInfo in members
            let value = getValue(memberInfo)
            let type = memberInfo is PropertyInfo prop ? prop.PropertyType : ((FieldInfo)memberInfo).FieldType
            select Serialize(value, type, memberInfo.Name, nestingLevel + 1, pathStack)
            into serializedMember
            where !string.IsNullOrEmpty(serializedMember)
            select identation + serializedMember).ToList();

        return string.Join(",\n", items);
    }

    private string Serialize<T>(T valueToSerialize, Type type, string name, int level, Stack<string> pathStack)
    {
        if (excludedTypes.Contains(type) || excludedProperties.Contains(name)) return string.Empty;
        if (valueToSerialize is null) return name + " = " + "null";

        var value = "";
        if (type.IsAssignableTo(typeof(IFormattable)))
            value = ((IFormattable)valueToSerialize).ToString(null, Culture);
        if (PropertyPrinters.TryGetValue(name, out var propertyPrinter))
            value = propertyPrinter(valueToSerialize);
        if (TypePrinters.TryGetValue(type, out var typePrinter))
            value = typePrinter(valueToSerialize);
        if (type.IsAssignableTo(typeof(IEnumerable)))
            value = PrintEnumerable((IEnumerable)valueToSerialize, level, pathStack);
        if (type.IsAssignableTo(typeof(KeyValuePair<,>)))
            value = PrintKeyValuePair(valueToSerialize, pathStack);
        if (type.IsAssignableTo(typeof(IDictionary)))
            value = PrintDictionary((IDictionary)valueToSerialize, level, pathStack);
        if (valueToSerialize is string stringValue && MaxLengths.TryGetValue(name, out var maxLength))
            value = stringValue[..Math.Min(value.Length, maxLength)];
        if (value == "")
            value = PrintToString(valueToSerialize, level + 1);
        if (value.Length > 0)
            return name + " = " + value;
        return string.Empty;
    }

    private string PrintEnumerable(IEnumerable? enumerable, int nestingLevel, Stack<string> pathStack)
    {
        var bracketIdentation = new string('\t', nestingLevel);
        var itemIdentation = new string('\t', nestingLevel + 1);
        if (enumerable is null) return "null";
        if (enumerable is string) return enumerable.ToString() ?? string.Empty;
        if (enumerable is IDictionary dictionary) return PrintDictionary(dictionary, nestingLevel, pathStack);
        var elementType = GetElementType(enumerable);
        if (elementType.IsAssignableTo(typeof(IEnumerable)))
            return
                $"[\n{string.Join(",\n", enumerable.Cast<object>().Select(item
                    => itemIdentation + PrintEnumerable((IEnumerable)item, nestingLevel + 1, pathStack)))}\n{bracketIdentation}]";
        if (finalTypes.Contains(elementType))
            return $"[{string.Join(", ", enumerable.Cast<object>().Select(item => item.ToString() ?? string.Empty))}]";
        var result =
            $"[\n{string.Join(",\n", enumerable.Cast<object>().Select(item
                => PrintToString(item, nestingLevel + 1, pathStack)))}\n{bracketIdentation}]";
        return result;
    }

    private string PrintDictionary(IDictionary? dictionary, int nestingLevel, Stack<string> pathStack)
    {
        if (dictionary is null) return "null";
        if (dictionary.Count == 0) return "{}";
        var bracketIdentation = new string('\t', nestingLevel);
        var itemIdentation = new string('\t', nestingLevel + 1);
        var serializedItems = dictionary.Keys.Cast<object>()
            .Select(key =>
                $"{PrintToString(key, nestingLevel + 1)}: {PrintToString(dictionary[key], nestingLevel + 1, pathStack)}");
        var result =
            $"{{\n{itemIdentation}{string.Join($",\n{itemIdentation}", serializedItems)}\n{bracketIdentation}}}";
        return result;
    }

    private string PrintKeyValuePair(object keyValuePair, Stack<string> pathStack)
    {
        return keyValuePair is KeyValuePair<object, object> keyValue
            ? $"{PrintToString(keyValue.Key, 0, pathStack)} = {PrintToString(keyValue.Value, 0, pathStack)}"
            : string.Empty;
    }

    private Type GetElementType(IEnumerable? enumerable)
    {
        if (enumerable is null) return typeof(object);
        var enumerableType = enumerable.GetType()
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerableType?.GetGenericArguments().FirstOrDefault() ?? typeof(object);
    }
}