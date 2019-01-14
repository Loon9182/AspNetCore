// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    internal class MvcEndpointDataSource : EndpointDataSource
    {
        private readonly IActionDescriptorCollectionProvider _actions;
        private readonly MvcEndpointInvokerFactory _invokerFactory;
        private readonly ParameterPolicyFactory _parameterPolicyFactory;

        // The following are protected by this lock for WRITES only. This pattern is similar
        // to DefaultActionDescriptorChangeProvider - see comments there for details on
        // all of the threading behaviors.
        private readonly object _lock = new object();
        private List<Endpoint> _endpoints;
        private CancellationTokenSource _cancellationTokenSource;
        private IChangeToken _changeToken;

        public MvcEndpointDataSource(
            IActionDescriptorCollectionProvider actions,
            MvcEndpointInvokerFactory invokerFactory,
            ParameterPolicyFactory parameterPolicyFactory)
        {
            if (actions == null)
            {
                throw new ArgumentNullException(nameof(actions));
            }

            if (invokerFactory == null)
            {
                throw new ArgumentNullException(nameof(invokerFactory));
            }

            if (parameterPolicyFactory == null)
            {
                throw new ArgumentNullException(nameof(parameterPolicyFactory));
            }

            _actions = actions;
            _invokerFactory = invokerFactory;
            _parameterPolicyFactory = parameterPolicyFactory;

            ConventionalEndpointInfos = new List<MvcEndpointInfo>();

            // IMPORTANT: this needs to be the last thing we do in the constructor. Change notifications can happen immediately!
            //
            // It's possible for someone to override the collection provider without providing
            // change notifications. If that's the case we won't process changes.
            if (actions is ActionDescriptorCollectionProvider collectionProviderWithChangeToken)
            {
                ChangeToken.OnChange(
                    () => collectionProviderWithChangeToken.GetChangeToken(),
                    UpdateEndpoints);
            }
        }

        public List<MvcEndpointInfo> ConventionalEndpointInfos { get; }

        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                Initialize();
                Debug.Assert(_changeToken != null);
                Debug.Assert(_endpoints != null);
                return _endpoints;
            }
        }

        public override IChangeToken GetChangeToken()
        {
            Initialize();
            Debug.Assert(_changeToken != null);
            Debug.Assert(_endpoints != null);
            return _changeToken;
        }

        private void Initialize()
        {
            if (_endpoints == null)
            {
                lock (_lock)
                {
                    if (_endpoints == null)
                    {
                        UpdateEndpoints();
                    }
                }
            }
        }

        private void UpdateEndpoints()
        {
            lock (_lock)
            {
                var endpoints = new List<Endpoint>();
                StringBuilder patternStringBuilder = null;

                foreach (var action in _actions.ActionDescriptors.Items)
                {
                    if (action.AttributeRouteInfo == null)
                    {
                        // In traditional conventional routing setup, the routes defined by a user have a static order
                        // defined by how they are added into the list. We would like to maintain the same order when building
                        // up the endpoints too.
                        //
                        // Start with an order of '1' for conventional routes as attribute routes have a default order of '0'.
                        // This is for scenarios dealing with migrating existing Router based code to Endpoint Routing world.
                        var conventionalRouteOrder = 1;

                        // Check each of the conventional patterns to see if the action would be reachable
                        // If the action and pattern are compatible then create an endpoint with the
                        // area/controller/action parameter parts replaced with literals
                        //
                        // e.g. {controller}/{action} with HomeController.Index and HomeController.Login
                        // would result in endpoints:
                        // - Home/Index
                        // - Home/Login
                        foreach (var endpointInfo in ConventionalEndpointInfos)
                        {
                            // An 'endpointInfo' is applicable if:
                            // 1. it has a parameter (or default value) for 'required' non-null route value
                            // 2. it does not have a parameter (or default value) for 'required' null route value
                            var isApplicable = true;
                            foreach (var routeKey in action.RouteValues.Keys)
                            {
                                if (!MatchRouteValue(action, endpointInfo, routeKey))
                                {
                                    isApplicable = false;
                                    break;
                                }
                            }

                            if (!isApplicable)
                            {
                                continue;
                            }

                            conventionalRouteOrder = CreateEndpoints(
                                endpoints,
                                ref patternStringBuilder,
                                action,
                                conventionalRouteOrder,
                                endpointInfo.ParsedPattern,
                                endpointInfo.MergedDefaults,
                                endpointInfo.Defaults,
                                endpointInfo.Name,
                                endpointInfo.DataTokens,
                                endpointInfo.ParameterPolicies,
                                suppressLinkGeneration: false,
                                suppressPathMatching: false);
                        }
                    }
                    else
                    {
                        var attributeRoutePattern = RoutePatternFactory.Parse(action.AttributeRouteInfo.Template);

                        CreateEndpoints(
                            endpoints,
                            ref patternStringBuilder,
                            action,
                            action.AttributeRouteInfo.Order,
                            attributeRoutePattern,
                            attributeRoutePattern.Defaults,
                            nonInlineDefaults: null,
                            action.AttributeRouteInfo.Name,
                            dataTokens: null,
                            allParameterPolicies: null,
                            action.AttributeRouteInfo.SuppressLinkGeneration,
                            action.AttributeRouteInfo.SuppressPathMatching);
                    }
                }

                // See comments in DefaultActionDescriptorCollectionProvider. These steps are done
                // in a specific order to ensure callers always see a consistent state.

                // Step 1 - capture old token
                var oldCancellationTokenSource = _cancellationTokenSource;

                // Step 2 - update endpoints
                _endpoints = endpoints;

                // Step 3 - create new change token
                _cancellationTokenSource = new CancellationTokenSource();
                _changeToken = new CancellationChangeToken(_cancellationTokenSource.Token);

                // Step 4 - trigger old token
                oldCancellationTokenSource?.Cancel();
            }
        }

        // CreateEndpoints processes the route pattern, replacing area/controller/action parameters with endpoint values
        // Because of default values it is possible for a route pattern to resolve to multiple endpoints
        private int CreateEndpoints(
            List<Endpoint> endpoints,
            ref StringBuilder patternStringBuilder,
            ActionDescriptor action,
            int routeOrder,
            RoutePattern routePattern,
            IReadOnlyDictionary<string, object> allDefaults,
            IReadOnlyDictionary<string, object> nonInlineDefaults,
            string name,
            RouteValueDictionary dataTokens,
            IDictionary<string, IList<IParameterPolicy>> allParameterPolicies,
            bool suppressLinkGeneration,
            bool suppressPathMatching)
        {
            var newPathSegments = routePattern.PathSegments.ToList();
            var hasLinkGenerationEndpoint = false;

            // This is required because we create modified copies of the route pattern using its segments
            // A segment with a parameter will automatically include its policies
            // Non-parameter policies need to be manually included
            var nonParameterPolicyValues = routePattern.ParameterPolicies
                .Where(p => routePattern.GetParameter(p.Key ?? string.Empty) == null && p.Value.Count > 0 && p.Value.First().ParameterPolicy != null) // Only GetParameter is required. Extra is for safety
                .Select(p => new KeyValuePair<string, object>(p.Key, p.Value.First().ParameterPolicy)) // Can only pass a single non-parameter to RouteParameter
                .ToArray();
            var nonParameterPolicies = RouteValueDictionary.FromArray(nonParameterPolicyValues);

            // Create a mutable copy
            var nonInlineDefaultsCopy = nonInlineDefaults != null
                ? new RouteValueDictionary(nonInlineDefaults)
                : null;

            var resolvedRouteValues = ResolveActionRouteValues(action, allDefaults);

            for (var i = 0; i < newPathSegments.Count; i++)
            {
                // Check if the pattern can be shortened because the remaining parameters are optional
                //
                // e.g. Matching pattern {controller=Home}/{action=Index} against HomeController.Index
                // can resolve to the following endpoints: (sorted by RouteEndpoint.Order)
                // - /
                // - /Home
                // - /Home/Index
                if (UseDefaultValuePlusRemainingSegmentsOptional(
                    i,
                    action,
                    resolvedRouteValues,
                    allDefaults,
                    ref nonInlineDefaultsCopy,
                    newPathSegments))
                {
                    // The route pattern has matching default values AND an optional parameter
                    // For link generation we need to include an endpoint with parameters and default values
                    // so the link is correctly shortened
                    // e.g. {controller=Home}/{action=Index}/{id=17}
                    if (!hasLinkGenerationEndpoint)
                    {
                        var ep = CreateEndpoint(
                            action,
                            resolvedRouteValues,
                            name,
                            GetPattern(ref patternStringBuilder, newPathSegments),
                            nonParameterPolicies,
                            newPathSegments,
                            nonInlineDefaultsCopy,
                            routeOrder++,
                            dataTokens,
                            suppressLinkGeneration,
                            true);
                        endpoints.Add(ep);

                        hasLinkGenerationEndpoint = true;
                    }

                    var subPathSegments = newPathSegments.Take(i);

                    var subEndpoint = CreateEndpoint(
                        action,
                        resolvedRouteValues,
                        name,
                        GetPattern(ref patternStringBuilder, subPathSegments),
                        nonParameterPolicies,
                        subPathSegments,
                        nonInlineDefaultsCopy,
                        routeOrder++,
                        dataTokens,
                        suppressLinkGeneration,
                        suppressPathMatching);
                    endpoints.Add(subEndpoint);
                }

                UpdatePathSegments(i, action, resolvedRouteValues, routePattern, newPathSegments, ref allParameterPolicies);
            }

            var finalEndpoint = CreateEndpoint(
                action,
                resolvedRouteValues,
                name,
                GetPattern(ref patternStringBuilder, newPathSegments),
                nonParameterPolicies,
                newPathSegments,
                nonInlineDefaultsCopy,
                routeOrder++,
                dataTokens,
                suppressLinkGeneration,
                suppressPathMatching);
            endpoints.Add(finalEndpoint);

            return routeOrder;

            string GetPattern(ref StringBuilder sb, IEnumerable<RoutePatternPathSegment> segments)
            {
                if (sb == null)
                {
                    sb = new StringBuilder();
                }

                RoutePatternWriter.WriteString(sb, segments);
                var rawPattern = sb.ToString();
                sb.Length = 0;

                return rawPattern;
            }
        }

        private static IDictionary<string, string> ResolveActionRouteValues(ActionDescriptor action, IReadOnlyDictionary<string, object> allDefaults)
        {
            Dictionary<string, string> resolvedRequiredValues = null;

            foreach (var kvp in action.RouteValues)
            {
                // Check whether there is a matching default value with a different case
                // e.g. {controller=HOME}/{action} with HomeController.Index will have route values:
                // - controller = HOME
                // - action = Index
                if (allDefaults.TryGetValue(kvp.Key, out var value) &&
                    value is string defaultValue &&
                    !string.Equals(kvp.Value, defaultValue, StringComparison.Ordinal) &&
                    string.Equals(kvp.Value, defaultValue, StringComparison.OrdinalIgnoreCase))
                {
                    if (resolvedRequiredValues == null)
                    {
                        resolvedRequiredValues = new Dictionary<string, string>(action.RouteValues, StringComparer.OrdinalIgnoreCase);
                    }

                    resolvedRequiredValues[kvp.Key] = defaultValue;
                }
            }

            return resolvedRequiredValues ?? action.RouteValues;
        }

        private void UpdatePathSegments(
            int i,
            ActionDescriptor action,
            IDictionary<string, string> resolvedRequiredValues,
            RoutePattern routePattern,
            List<RoutePatternPathSegment> newPathSegments,
            ref IDictionary<string, IList<IParameterPolicy>> allParameterPolicies)
        {
            List<RoutePatternPart> segmentParts = null; // Initialize only as needed
            var segment = newPathSegments[i];
            for (var j = 0; j < segment.Parts.Count; j++)
            {
                var part = segment.Parts[j];

                if (part is RoutePatternParameterPart parameterPart)
                {
                    if (resolvedRequiredValues.TryGetValue(parameterPart.Name, out var parameterRouteValue))
                    {
                        if (segmentParts == null)
                        {
                            segmentParts = segment.Parts.ToList();
                        }
                        if (allParameterPolicies == null)
                        {
                            allParameterPolicies = MvcEndpointInfo.BuildParameterPolicies(routePattern.Parameters, _parameterPolicyFactory);
                        }

                        // Route value could be null if it is a "known" route value.
                        // Do not use the null value to de-normalize the route pattern,
                        // instead leave the parameter unchanged.
                        // e.g.
                        //     RouteValues will contain a null "page" value if there are Razor pages
                        //     Skip replacing the {page} parameter
                        if (parameterRouteValue != null)
                        {
                            if (allParameterPolicies.TryGetValue(parameterPart.Name, out var parameterPolicies))
                            {
                                // Check if the parameter has a transformer policy
                                // Use the first transformer policy
                                for (var k = 0; k < parameterPolicies.Count; k++)
                                {
                                    if (parameterPolicies[k] is IOutboundParameterTransformer parameterTransformer)
                                    {
                                        parameterRouteValue = parameterTransformer.TransformOutbound(parameterRouteValue);
                                        break;
                                    }
                                }
                            }

                            segmentParts[j] = RoutePatternFactory.LiteralPart(parameterRouteValue);
                        }
                    }
                }
            }

            // A parameter part was replaced so replace segment with updated parts
            if (segmentParts != null)
            {
                newPathSegments[i] = RoutePatternFactory.Segment(segmentParts);
            }
        }

        private bool UseDefaultValuePlusRemainingSegmentsOptional(
            int segmentIndex,
            ActionDescriptor action,
            IDictionary<string, string> resolvedRequiredValues,
            IReadOnlyDictionary<string, object> allDefaults,
            ref RouteValueDictionary nonInlineDefaults,
            List<RoutePatternPathSegment> pathSegments)
        {
            // Check whether the remaining segments are all optional and one or more of them is
            // for area/controller/action and has a default value
            var usedDefaultValue = false;

            for (var i = segmentIndex; i < pathSegments.Count; i++)
            {
                var segment = pathSegments[i];
                for (var j = 0; j < segment.Parts.Count; j++)
                {
                    var part = segment.Parts[j];
                    if (part.IsParameter && part is RoutePatternParameterPart parameterPart)
                    {
                        if (allDefaults.TryGetValue(parameterPart.Name, out var v))
                        {
                            if (resolvedRequiredValues.TryGetValue(parameterPart.Name, out var routeValue))
                            {
                                if (string.Equals(v as string, routeValue, StringComparison.OrdinalIgnoreCase))
                                {
                                    usedDefaultValue = true;
                                    continue;
                                }
                            }
                            else
                            {
                                if (nonInlineDefaults == null)
                                {
                                    nonInlineDefaults = new RouteValueDictionary();
                                }
                                nonInlineDefaults.TryAdd(parameterPart.Name, v);

                                usedDefaultValue = true;
                                continue;
                            }
                        }

                        if (parameterPart.IsOptional || parameterPart.IsCatchAll)
                        {
                            continue;
                        }
                    }
                    else if (part.IsSeparator && part is RoutePatternSeparatorPart separatorPart
                        && separatorPart.Content == ".")
                    {
                        // Check if this pattern ends in an optional extension, e.g. ".{ext?}"
                        // Current literal must be "." and followed by a single optional parameter part
                        var nextPartIndex = j + 1;

                        if (nextPartIndex == segment.Parts.Count - 1
                            && segment.Parts[nextPartIndex].IsParameter
                            && segment.Parts[nextPartIndex] is RoutePatternParameterPart extensionParameterPart
                            && extensionParameterPart.IsOptional)
                        {
                            continue;
                        }
                    }

                    // Stop because there is a non-optional/non-defaulted trailing value
                    return false;
                }
            }

            return usedDefaultValue;
        }

        private bool MatchRouteValue(ActionDescriptor action, MvcEndpointInfo endpointInfo, string routeKey)
        {
            if (!action.RouteValues.TryGetValue(routeKey, out var actionValue) || string.IsNullOrWhiteSpace(actionValue))
            {
                // Action does not have a value for this routeKey, most likely because action is not in an area
                // Check that the pattern does not have a parameter for the routeKey
                var matchingParameter = endpointInfo.ParsedPattern.GetParameter(routeKey);
                if (matchingParameter == null &&
                    (!endpointInfo.ParsedPattern.Defaults.TryGetValue(routeKey, out var value) ||
                    !string.IsNullOrEmpty(Convert.ToString(value))))
                {
                    return true;
                }
            }
            else
            {
                if (endpointInfo.MergedDefaults != null && string.Equals(actionValue, endpointInfo.MergedDefaults[routeKey] as string, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var matchingParameter = endpointInfo.ParsedPattern.GetParameter(routeKey);
                if (matchingParameter != null)
                {
                    // Check that the value matches against constraints on that parameter
                    // e.g. For {controller:regex((Home|Login))} the controller value must match the regex
                    if (endpointInfo.ParameterPolicies.TryGetValue(routeKey, out var parameterPolicies))
                    {
                        foreach (var policy in parameterPolicies)
                        {
                            if (policy is IRouteConstraint constraint
                                && !constraint.Match(httpContext: null, NullRouter.Instance, routeKey, new RouteValueDictionary(action.RouteValues), RouteDirection.IncomingRequest))
                            {
                                // Did not match constraint
                                return false;
                            }
                        }
                    }

                    return true;
                }
            }

            return false;
        }

        private RouteEndpoint CreateEndpoint(
            ActionDescriptor action,
            IDictionary<string, string> actionRouteValues,
            string routeName,
            string patternRawText,
            object nonParameterPolicies,
            IEnumerable<RoutePatternPathSegment> segments,
            object nonInlineDefaults,
            int order,
            RouteValueDictionary dataTokens,
            bool suppressLinkGeneration,
            bool suppressPathMatching)
        {
            RequestDelegate requestDelegate = (context) =>
            {
                var routeData = context.GetRouteData();

                var actionContext = new ActionContext(context, routeData, action);

                var invoker = _invokerFactory.CreateInvoker(actionContext);
                return invoker.InvokeAsync();
            };

            var defaults = new RouteValueDictionary(nonInlineDefaults);
            EnsureRequiredValuesInDefaults(actionRouteValues, defaults, segments);

            var metadataCollection = BuildEndpointMetadata(
                action,
                routeName,
                new RouteValueDictionary(actionRouteValues),
                dataTokens,
                suppressLinkGeneration,
                suppressPathMatching);

            var endpoint = new RouteEndpoint(
                requestDelegate,
                RoutePatternFactory.Pattern(patternRawText, defaults, nonParameterPolicies, segments),
                order,
                metadataCollection,
                action.DisplayName);

            return endpoint;
        }

        private static EndpointMetadataCollection BuildEndpointMetadata(
            ActionDescriptor action,
            string routeName,
            RouteValueDictionary requiredValues,
            RouteValueDictionary dataTokens,
            bool suppressLinkGeneration,
            bool suppressPathMatching)
        {
            var metadata = new List<object>();

            // Add action metadata first so it has a low precedence
            if (action.EndpointMetadata != null)
            {
                metadata.AddRange(action.EndpointMetadata);
            }

            metadata.Add(action);

            if (dataTokens != null)
            {
                metadata.Add(new DataTokensMetadata(dataTokens));
            }

            metadata.Add(new RouteValuesAddressMetadata(routeName, requiredValues));

            // Add filter descriptors to endpoint metadata
            if (action.FilterDescriptors != null && action.FilterDescriptors.Count > 0)
            {
                metadata.AddRange(action.FilterDescriptors.OrderBy(f => f, FilterDescriptorOrderComparer.Comparer)
                    .Select(f => f.Filter));
            }

            if (action.ActionConstraints != null && action.ActionConstraints.Count > 0)
            {
                // We explicitly convert a few types of action constraints into MatcherPolicy+Metadata
                // to better integrate with the DFA matcher.
                //
                // Other IActionConstraint data will trigger a back-compat path that can execute
                // action constraints.
                foreach (var actionConstraint in action.ActionConstraints)
                {
                    if (actionConstraint is HttpMethodActionConstraint httpMethodActionConstraint &&
                        !metadata.OfType<HttpMethodMetadata>().Any())
                    {
                        metadata.Add(new HttpMethodMetadata(httpMethodActionConstraint.HttpMethods));
                    }
                    else if (actionConstraint is ConsumesAttribute consumesAttribute &&
                        !metadata.OfType<ConsumesMetadata>().Any())
                    {
                        metadata.Add(new ConsumesMetadata(consumesAttribute.ContentTypes.ToArray()));
                    }
                    else if (!metadata.Contains(actionConstraint))
                    {
                        // The constraint might have been added earlier, e.g. it is also a filter descriptor
                        metadata.Add(actionConstraint);
                    }
                }
            }

            if (suppressLinkGeneration)
            {
                metadata.Add(new SuppressLinkGenerationMetadata());
            }

            if (suppressPathMatching)
            {
                metadata.Add(new SuppressMatchingMetadata());
            }

            var metadataCollection = new EndpointMetadataCollection(metadata);
            return metadataCollection;
        }

        // Ensure route values are a subset of defaults
        // Examples:
        //
        // Template: {controller}/{action}/{category}/{id?}
        // Defaults(in-line or non in-line): category=products
        // Required values: controller=foo, action=bar
        // Final constructed pattern: foo/bar/{category}/{id?}
        // Final defaults: controller=foo, action=bar, category=products
        //
        // Template: {controller=Home}/{action=Index}/{category=products}/{id?}
        // Defaults: controller=Home, action=Index, category=products
        // Required values: controller=foo, action=bar
        // Final constructed pattern: foo/bar/{category}/{id?}
        // Final defaults: controller=foo, action=bar, category=products
        private void EnsureRequiredValuesInDefaults(
            IDictionary<string, string> routeValues,
            RouteValueDictionary defaults,
            IEnumerable<RoutePatternPathSegment> segments)
        {
            foreach (var kvp in routeValues)
            {
                if (kvp.Value != null)
                {
                    defaults[kvp.Key] = kvp.Value;
                }
            }
        }
    }
}