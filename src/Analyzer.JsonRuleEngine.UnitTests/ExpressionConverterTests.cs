﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Azure.Templates.Analyzer.RuleEngines.JsonEngine.Converters;
using Microsoft.Azure.Templates.Analyzer.RuleEngines.JsonEngine.Schemas;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.Templates.Analyzer.RuleEngines.JsonEngine.UnitTests
{
    [TestClass]
    public class ExpressionConverterTests
    {
        // Dictionary of property names to PropertyInfo
        private static readonly Dictionary<string, PropertyInfo> leafExpressionJsonProperties =
            typeof(LeafExpressionDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<JsonPropertyAttribute>()))
            .Where(property => property.Attribute != null)
            .ToDictionary(property => property.Attribute.PropertyName ?? property.Property.Name, property => property.Property, StringComparer.OrdinalIgnoreCase);

        [DataTestMethod]
        [DataRow("hasValue", true, DisplayName = "{\"HasValue\": true}")]
        [DataRow("exists", false, DisplayName = "{\"Exists\": false}")]
        [DataRow("equals", "someString", DisplayName = "{\"Equals\": \"someString\"}")]
        [DataRow("notEquals", 0, DisplayName = "{\"NotEquals\": 0}")]
        public void ReadJson_LeafWithValidOperator_ReturnsCorrectTypeAndValues(string operatorProperty, object operatorValue)
        {
            var @object = ReadJson($@"
                {{
                    ""resourceType"": ""someResource/resourceType"",
                    ""path"": ""some.json.path"",
                    ""{operatorProperty}"": {JsonConvert.SerializeObject(operatorValue)}
                }}");

            Assert.AreEqual(typeof(LeafExpressionDefinition), @object.GetType());

            var expression = @object as LeafExpressionDefinition;
            Assert.AreEqual("some.json.path", expression.Path);
            Assert.AreEqual("someResource/resourceType", expression.ResourceType);

            // Iterate through possible expressions and ensure only the specified one has a value
            foreach (var expressionProperty in leafExpressionJsonProperties)
            {
                var parsedValue = expressionProperty.Value.GetValue(expression);
                if (expressionProperty.Key.Equals(operatorProperty, StringComparison.OrdinalIgnoreCase))
                {
                    if (expressionProperty.Value.PropertyType == typeof(bool?))
                    {
                        Assert.AreEqual(operatorValue, (bool)parsedValue);
                    }
                    else if (expressionProperty.Value.PropertyType == typeof(string))
                    {
                        Assert.AreEqual(operatorValue, (string)parsedValue);
                    }
                    else
                    {
                        Assert.AreEqual(new JValue(operatorValue), parsedValue);
                    }
                }
                else
                {
                    Assert.IsNull(parsedValue);
                }
            }
        }

        [DataTestMethod]
        [DataRow("allOf", typeof(AllOfExpressionDefinition), DisplayName = "AllOf Expression")]
        [DataRow("anyOf", typeof(AnyOfExpressionDefinition), DisplayName = "AnyOf Expression")]
        public void ReadJson_ValidStructuredExpression_ReturnsCorrectTypeAndValues(string expressionName, Type expressionDefinitionType)
        {
            var @object = ReadJson($@"
                {{
                    ""resourceType"": ""someResource/resourceType"",
                    ""path"": ""some.json.path"",
                    ""{expressionName}"": [ 
                        {{
                            ""path"": ""some.other.path"", 
                            ""hasValue"": true 
                        }}, 
                        {{
                            ""path"": ""some.other.path"", 
                            ""equals"": true 
                        }}
                    ]
                }}");

            Assert.AreEqual(expressionDefinitionType, @object.GetType());

            ExpressionDefinition expressionDefinition = @object as ExpressionDefinition;

            Assert.AreEqual("some.json.path", expressionDefinition.Path);
            Assert.AreEqual("someResource/resourceType", expressionDefinition.ResourceType);
        }

        [DataTestMethod]
        [DataRow("hasValue", "string", DisplayName = "\"HasValue\": \"string\"")]
        [DataRow("exists", new int[0], DisplayName = "\"Exists\": []")]
        [ExpectedException(typeof(JsonReaderException))]
        public void ReadJson_LeafWithInvalidOperator_ThrowsParsingException(string operatorProperty, object operatorValue)
        {
            ReadJson($@"
                {{
                    ""resourceType"": ""resourceType"",
                    ""path"": ""path"",
                    ""{operatorProperty}"": {JsonConvert.SerializeObject(operatorValue)}
                }}");
        }

        [DataTestMethod]
        [DataRow("allOf", "string", DisplayName = "\"AllOf\": \"string\"")]
        [DataRow("anyOf", "string", DisplayName = "\"AnyOf\": \"string\"")]
        [DynamicData(nameof(EmptyAllOfArray), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetAllOfIsEmptyDynamicDataDisplayName))]
        [DynamicData(nameof(EmptyAnyOfArray), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetAnyOfIsEmptyDynamicDataDisplayName))]
        [DynamicData(nameof(AllOfArrayWithNull), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetAllOfHasNullItemDynamicDataDisplayName))]
        [DynamicData(nameof(AnyOfArrayWithNull), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetAnyOfHasNullItemDynamicDataDisplayName))]
        [ExpectedException(typeof(JsonException), AllowDerivedTypes = true)]
        public void ReadJson_StructuredExpressionWithInvalidExpression_ThrowsParsingException(string operatorProperty, object operatorValue)
        {
            ReadJson($@"
                {{
                    ""resourceType"": ""resourceType"",
                    ""path"": ""path"",
                    ""{operatorProperty}"": {JsonConvert.SerializeObject(operatorValue)}
                }}");
        }

        [TestMethod]
        [DataRow(DisplayName = "No operators")]
        [DataRow("hasValue", true, "exists", true, DisplayName = "HasValue and Exists")]
        [ExpectedException(typeof(JsonException))]
        public void ReadJson_LeafWithInvalidOperatorCount_ThrowsParsingException(params object[] operators)
        {
            var leafDefinition = "{\"resourceType\": \"resource\", \"path\": \"path\"";

            if (operators.Length % 2 != 0)
            {
                Assert.Fail("Must provide an operator value for each operator property.");
            }

            int index = 0;
            foreach (var op in operators)
            {
                if (index++ % 2 == 0)
                {
                    if (!(op is string))
                    {
                        Assert.Fail("Operator property (first of each pair) must be a string");
                    }
                    leafDefinition += $", \"{op}\": ";
                }
                else
                {
                    var jsonValue = JsonConvert.SerializeObject(op);
                    leafDefinition += jsonValue;
                }
            }

            leafDefinition += "}";

            try
            {
                ReadJson(leafDefinition);
            }
            catch (Exception e)
            {
                Assert.IsTrue(e.Message.IndexOf(operators.Length > 0 ? "too many" : "invalid", StringComparison.OrdinalIgnoreCase) >= 0);
                throw;
            }
        }

        [TestMethod]
        public void ReadJson_NullTokenType_ReturnsNull()
        {
            var nullTokenReader = JObject.Parse("{\"Key\": null}").CreateReader();

            nullTokenReader.Read(); // Read start of object
            nullTokenReader.Read(); // Read Key
            nullTokenReader.Read(); // Read value (null)

            Assert.IsNull(
                new ExpressionConverter().ReadJson(
                    nullTokenReader,
                    typeof(ExpressionDefinition),
                    null,
                    JsonSerializer.CreateDefault()));
        }

        [TestMethod]
        public void CanRead_ReturnsTrue()
        {
            Assert.IsTrue(new ExpressionConverter().CanRead);
        }

        [TestMethod]
        public void CanWrite_ReturnsFalse()
        {
            Assert.IsFalse(new ExpressionConverter().CanWrite);
        }

        [TestMethod]
        [ExpectedException(typeof(NotImplementedException))]
        public void WriteJson_ThrowsException()
        {
            new ExpressionConverter().WriteJson(null, null, null);
        }

        private static object ReadJson(string jsonString)
            => new ExpressionConverter().ReadJson(
                JObject.Parse(jsonString).CreateReader(),
                typeof(ExpressionDefinition),
                null,
                JsonSerializer.CreateDefault());

        static IEnumerable<object[]> EmptyAllOfArray()
        {
            yield return new object[] { "allOf", new object[0] };
        }

        public static string GetAllOfIsEmptyDynamicDataDisplayName(MethodInfo methodInfo, object[] data)
        {
            return "\"AllOf\": []";
        }

        static IEnumerable<object[]> EmptyAnyOfArray()
        {
            yield return new object[] { "anyOf", new object[0] };
        }

        public static string GetAnyOfIsEmptyDynamicDataDisplayName(MethodInfo methodInfo, object[] data)
        {
            return "\"AnyOf\": []";
        }

        static IEnumerable<object[]> AllOfArrayWithNull()
        {
            yield return new object[] { "allOf", new object[1] { null } };
        }

        public static string GetAllOfHasNullItemDynamicDataDisplayName(MethodInfo methodInfo, object[] data)
        {
            return "\"AllOf\": [ null ]";
        }

        static IEnumerable<object[]> AnyOfArrayWithNull()
        {
            yield return new object[] { "anyOf", new object[1] { null } };
        }

        public static string GetAnyOfHasNullItemDynamicDataDisplayName(MethodInfo methodInfo, object[] data)
        {
            return "\"AnyOf\": [ null ]";
        }
    }
}