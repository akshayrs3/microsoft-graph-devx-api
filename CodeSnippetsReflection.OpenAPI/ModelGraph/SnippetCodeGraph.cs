﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using CodeSnippetsReflection.StringExtensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Services;

namespace CodeSnippetsReflection.OpenAPI.ModelGraph
{
    public record SnippetCodeGraph
    {

        private static readonly Regex nestedStatementRegex = new(@"(\w+)(\([^)]+\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly CodeProperty EMPTY_PROPERTY = new() { Name = null, Value = null, Children = null, PropertyType = PropertyType.Default };

        public SnippetCodeGraph(SnippetModel snippetModel)
        {
            ResponseSchema = snippetModel.ResponseSchema;
            HttpMethod = snippetModel.Method;
            Nodes = snippetModel.PathNodes;
            Headers = parseHeaders(snippetModel);
            Options = Enumerable.Empty<CodeProperty>();
            Parameters = parseParameters(snippetModel);
            Body = parseBody(snippetModel);
        }

        public OpenApiSchema ResponseSchema
        {
            get; set;
        }
        public HttpMethod HttpMethod
        {
            get; set;
        }

        public IEnumerable<CodeProperty> Headers
        {
            get; set;
        }
        public IEnumerable<CodeProperty> Options
        {
            get; set;
        }

        public IEnumerable<CodeProperty> Parameters
        {
            get; set;
        }

        public CodeProperty Body
        {
            get; set;
        }

        public IEnumerable<OpenApiUrlTreeNode> Nodes
        {
            get; set;
        }

       
        public Boolean HasHeaders()
        {
            return Headers.Any();
        }

        public Boolean HasOptions()
        {
            return Options.Any();
        }

        public Boolean HasParameters()
        {
            return Parameters.Any();
        }

        public Boolean HasBody()
        {
            return Body.PropertyType != PropertyType.Default;
        }


        ///
        /// Parses Headers Filtering Out 'Host'
        ///
        private static IEnumerable<CodeProperty> parseHeaders(SnippetModel snippetModel)
        {
            return snippetModel.RequestHeaders.Where(h => !h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                .Select(h => new CodeProperty { Name = h.Key, Value = h.Value?.FirstOrDefault(), Children = null, PropertyType = PropertyType.String })
                .ToList();
        }


        private static List<CodeProperty> parseParameters(SnippetModel snippetModel)
        {
            var parameters = new List<CodeProperty>();
            if (!string.IsNullOrEmpty(snippetModel.QueryString))
            {
                var (queryString, replacements) = ReplaceNestedOdataQueryParameters(snippetModel.QueryString);

                NameValueCollection queryCollection = HttpUtility.ParseQueryString(queryString);
                foreach (String key in queryCollection.AllKeys)
                {
                    parameters.Add(new() { Name = NormalizeQueryParameterName(key), Value = GetQueryParameterValue(queryCollection[key], replacements), PropertyType = PropertyType.String });
                }

            }
            return parameters;
        }

        private static string NormalizeQueryParameterName(string queryParam) => System.Web.HttpUtility.UrlDecode(queryParam.TrimStart('$').ToFirstCharacterLowerCase());

        private static (string, Dictionary<string, string>) ReplaceNestedOdataQueryParameters(string queryParams)
        {
            var replacements = new Dictionary<string, string>();
            var matches = nestedStatementRegex.Matches(queryParams);
            if (matches.Any())
                foreach (var groups in matches.Select(m => m.Groups))
                {
                    var key = groups[1].Value;
                    var value = groups[2].Value;
                    replacements.Add(key, value);
                    queryParams = queryParams.Replace(value, string.Empty);
                }
            return (queryParams, replacements);
        }

        private static string GetQueryParameterValue(string originalValue, Dictionary<string, string> replacements)
        {
            var escapedParam = System.Web.HttpUtility.UrlDecode(originalValue);
            if (escapedParam.Equals("true", StringComparison.OrdinalIgnoreCase) || escapedParam.Equals("false", StringComparison.OrdinalIgnoreCase))
                return escapedParam.ToLowerInvariant();
            else if (int.TryParse(escapedParam, out var intValue))
                return intValue.ToString();
            else
            {
                return escapedParam.Split(',')
                    .Select(v => replacements.ContainsKey(v) ? v + replacements[v] : v)
                    .Aggregate((a, b) => $"{a},{b}");
            }
        }

        private static CodeProperty parseBody(SnippetModel snippetModel)
        {
            if (string.IsNullOrWhiteSpace(snippetModel?.RequestBody))
                return EMPTY_PROPERTY;

            switch (snippetModel.ContentType?.Split(';').First().ToLowerInvariant())
            {
                case "application/json":
                    return TryParseBody(snippetModel);
                case "application/octet-stream":
                    return new() { Name = null, Value = snippetModel.RequestBody?.ToString(), Children = null, PropertyType = PropertyType.Binary };
                default:
                    return TryParseBody(snippetModel);//in case the content type header is missing but we still have a json payload
            }
        }

        private static string ComputeRequestBody(SnippetModel snippetModel)
        {
            var nodes = snippetModel.PathNodes;
            if (!(nodes?.Any() ?? false)) return string.Empty;

            var nodeName = nodes.Where(x => !x.Segment.IsCollectionIndex())
                .Select(x =>
                {
                    if (x.Segment.IsFunction())
                        return x.Segment.Split('.').Last();
                    else
                        return x.Segment;
                })
                .Last()
                .ToFirstCharacterUpperCase();

            var singularNodeName = nodeName[nodeName.Length - 1] == 's' ? nodeName.Substring(0, nodeName.Length - 1) : nodeName;

            if (nodes.Last()?.Segment?.IsCollectionIndex() == true)
                return singularNodeName;
            else
                return $"{nodeName}PostRequestBody";

        }

        private static CodeProperty TryParseBody(SnippetModel snippetModel)
        {
            if (!snippetModel.IsRequestBodyValid)
                throw new InvalidOperationException($"Unsupported content type: {snippetModel.ContentType}");

            using var parsedBody = JsonDocument.Parse(snippetModel.RequestBody, new JsonDocumentOptions { AllowTrailingCommas = true });
            var schema = snippetModel.RequestSchema;
            var className = schema.GetSchemaTitle().ToFirstCharacterUpperCase() ?? ComputeRequestBody(snippetModel);
            return parseJsonObjectValue(className, parsedBody.RootElement, schema);
        }

        private static CodeProperty parseJsonObjectValue(String rootPropertyName, JsonElement value, OpenApiSchema schema)
        {
            var children = new List<CodeProperty>();

            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Expected JSON object and got {value.ValueKind}");

            var propertiesAndSchema = value.EnumerateObject()
                                            .Select(x => new Tuple<JsonProperty, OpenApiSchema>(x, schema.GetPropertySchema(x.Name)));
            foreach (var propertyAndSchema in propertiesAndSchema.Where(x => x.Item2 != null))
            {
                var propertyName = propertyAndSchema.Item1.Name.ToFirstCharacterLowerCase();
                children.Add(parseProperty(propertyName, propertyAndSchema.Item1.Value, propertyAndSchema.Item2));
            }

            var propertiesWithoutSchema = propertiesAndSchema.Where(x => x.Item2 == null).Select(x => x.Item1);
            if (propertiesWithoutSchema.Any())
            {

                var additionalChildren = new List<CodeProperty>();
                foreach (var property in propertiesWithoutSchema)
                    additionalChildren.Add(parseProperty(property.Name, property.Value, null));

                if (additionalChildren.Any())
                    children.Add(new CodeProperty { Name = "additionalData", PropertyType = PropertyType.Map, Children = additionalChildren });
            }

            return new CodeProperty { Name = rootPropertyName, PropertyType = PropertyType.Object, Children = children };
        }

        private static String escapeSpecialCharacters(string value)
        {
            return value?.Replace("\"", "\\\"")?.Replace("\n", "\\n")?.Replace("\r", "\\r");
        }

        private static CodeProperty evaluateStringProperty(string propertyName, JsonElement value, OpenApiSchema propSchema)
        {
            if (propSchema?.Format?.Equals("base64url", StringComparison.OrdinalIgnoreCase) ?? false)
                return new CodeProperty { Name = propertyName, Value = value.GetString(), PropertyType = PropertyType.Base64Url, Children = new List<CodeProperty>() };

            if (propSchema?.Format?.Equals("date-time", StringComparison.OrdinalIgnoreCase) ?? false)
                return new CodeProperty { Name = propertyName, Value = value.GetString(), PropertyType = PropertyType.Date, Children = new List<CodeProperty>() };


            var enumSchema = propSchema?.AnyOf.FirstOrDefault(x => x.Enum.Count > 0);
            if (enumSchema == null)
                return new CodeProperty { Name = propertyName, Value = escapeSpecialCharacters(value.GetString()), PropertyType = PropertyType.String, Children = new List<CodeProperty>() };


            var propValue = String.IsNullOrWhiteSpace(value.GetString()) ? null : $"{enumSchema.Title.ToFirstCharacterUpperCase()}.{value.GetString().ToFirstCharacterUpperCase()}";
            return new CodeProperty { Name = propertyName, Value = propValue, PropertyType = PropertyType.Enum, Children = new List<CodeProperty>() };
        }

        private static CodeProperty parseProperty(string propertyName, JsonElement value, OpenApiSchema propSchema)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return evaluateStringProperty(propertyName, value, propSchema);
                case JsonValueKind.Number:
                    return new CodeProperty { Name = propertyName, Value = $"{value}", PropertyType = PropertyType.Number, Children = new List<CodeProperty>() };
                case JsonValueKind.False:
                case JsonValueKind.True:
                    return new CodeProperty { Name = propertyName, Value = value.GetBoolean().ToString(), PropertyType = PropertyType.Boolean, Children = new List<CodeProperty>() };
                case JsonValueKind.Null:
                    return new CodeProperty { Name = propertyName, Value = "null", PropertyType = PropertyType.Null, Children = new List<CodeProperty>() };
                case JsonValueKind.Object:
                    if (propSchema != null)
                        return parseJsonObjectValue(propertyName, value, propSchema);
                    else
                        return parseAnonymousObjectValues(propertyName, value, propSchema);
                case JsonValueKind.Array:
                    return parseJsonArrayValue(propertyName, value, propSchema);
                default:
                    throw new NotImplementedException($"Unsupported JsonValueKind: {value.ValueKind}");
            }
        }

        private static CodeProperty parseJsonArrayValue(string propertyName, JsonElement value, OpenApiSchema schema)
        {
            var children = value.EnumerateArray().Select(item => parseProperty(schema.GetSchemaTitle().ToFirstCharacterUpperCase(), item, schema)).ToList();
            return new CodeProperty { Name = propertyName, Value = null, PropertyType = PropertyType.Array, Children = children };
        }

        private static CodeProperty parseAnonymousObjectValues(string propertyName, JsonElement value, OpenApiSchema schema)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Expected JSON object and got {value.ValueKind}");

            var children = new List<CodeProperty>();
            var propertiesAndSchema = value.EnumerateObject()
                                            .Select(x => new Tuple<JsonProperty, OpenApiSchema>(x, schema.GetPropertySchema(x.Name)));
            foreach (var propertyAndSchema in propertiesAndSchema)
            {
                children.Add(parseProperty(propertyAndSchema.Item1.Name.ToFirstCharacterLowerCase(), propertyAndSchema.Item1.Value, propertyAndSchema.Item2));
            }

            return new CodeProperty { Name = propertyName, Value = null, PropertyType = PropertyType.Object, Children = children };
        }
    }

}