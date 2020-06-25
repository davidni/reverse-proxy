// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.ReverseProxy.Abstractions;
using Microsoft.ReverseProxy.ServiceFabric.Utilities;

namespace Microsoft.ReverseProxy.ServiceFabric
{
    /// <summary>
    /// Helper class to parse configuration labels of the gateway into actual objects.
    /// </summary>
    // TODO: this is probably something that can be used in other integration modules apart from Service Fabric. Consider extracting to a general class.
    internal static class LabelsParser
    {
        // TODO: decide which labels are needed and which default table (and to what values)
        // Also probably move these defaults to the corresponding config entities.
        internal static readonly int DefaultCircuitbreakerMaxConcurrentRequests = 0;
        internal static readonly int DefaultCircuitbreakerMaxConcurrentRetries = 0;
        internal static readonly double DefaultQuotaAverage = 0;
        internal static readonly double DefaultQuotaBurst = 0;
        internal static readonly int DefaultPartitionCount = 0;
        internal static readonly string DefaultPartitionKeyExtractor = null;
        internal static readonly string DefaultPartitioningAlgorithm = "SHA256";
        internal static readonly int? DefaultRoutePriority = null;

        private static readonly Regex _allowedRouteNamesRegex = new Regex("^[a-zA-Z0-9_-]+$");

        internal static TValue GetLabel<TValue>(Dictionary<string, string> labels, string key, TValue defaultValue)
        {
            if (!labels.TryGetValue(key, out var value))
            {
                return defaultValue;
            }
            else
            {
                try
                {
                    return (TValue)TypeDescriptor.GetConverter(typeof(TValue)).ConvertFromString(value);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException)
                {
                    throw new ConfigException($"Could not convert label {key}='{value}' to type {typeof(TValue).FullName}.", ex);
                }
            }
        }

        // TODO: optimize this method
        internal static List<ProxyRoute> BuildRoutes(Uri serviceName, Dictionary<string, string> labels)
        {
            var backendId = GetClusterId(serviceName, labels);

            // Look for route IDs
            const string RoutesLabelsPrefix = "IslandGateway.Routes.";
            var routesNames = new HashSet<string>();
            foreach (var kvp in labels)
            {
                if (kvp.Key.Length > RoutesLabelsPrefix.Length && kvp.Key.StartsWith(RoutesLabelsPrefix, StringComparison.Ordinal))
                {
                    var suffix = kvp.Key.Substring(RoutesLabelsPrefix.Length);
                    var routeNameLength = suffix.IndexOf('.');
                    if (routeNameLength == -1)
                    {
                        // No route name encoded, the key is not valid. Throwing would suggest we actually check for all invalid keys, so just ignore.
                        continue;
                    }
                    var routeName = suffix.Substring(0, routeNameLength);
                    if (!_allowedRouteNamesRegex.IsMatch(routeName))
                    {
                        throw new ConfigException($"Invalid route name '{routeName}', should only contain alphanumerical characters, underscores or hyphens.");
                    }
                    routesNames.Add(routeName);
                }
            }

            // Build the routes
            var routes = new List<ProxyRoute>();
            foreach (var routeName in routesNames)
            {
                var thisRoutePrefix = $"{RoutesLabelsPrefix}{routeName}";
                var metadata = new Dictionary<string, string>();
                foreach (var kvp in labels)
                {
                    if (kvp.Key.StartsWith($"{thisRoutePrefix}.Metadata.", StringComparison.Ordinal))
                    {
                        metadata.Add(kvp.Key.Substring($"{thisRoutePrefix}.Metadata.".Length), kvp.Value);
                    }
                }

                if (!labels.TryGetValue($"{thisRoutePrefix}.Host", out var host))
                {
                    throw new ConfigException($"Missing '{thisRoutePrefix}.Host'.");
                }
                labels.TryGetValue($"{thisRoutePrefix}.Path", out var path);

                var route = new ProxyRoute
                {
                    RouteId = $"{Uri.EscapeDataString(backendId)}:{Uri.EscapeDataString(routeName)}",
                    Match =
                    {
                         Host = host,
                         Path = path,
                    },
                    Priority = GetLabel(labels, $"{thisRoutePrefix}.Priority", DefaultRoutePriority),
                    ClusterId = backendId,
                    Metadata = metadata,
                };
                routes.Add(route);
            }
            return routes;
        }

        internal static Cluster BuildCluster(Uri serviceName, Dictionary<string, string> labels)
        {
            var clusterMetadata = new Dictionary<string, string>();
            const string BackendMetadataKeyPrefix = "IslandGateway.Backend.Metadata.";
            foreach (var item in labels)
            {
                if (item.Key.StartsWith(BackendMetadataKeyPrefix, StringComparison.Ordinal))
                {
                    clusterMetadata[item.Key.Substring(BackendMetadataKeyPrefix.Length)] = item.Value;
                }
            }

            var clusterId = GetClusterId(serviceName, labels);

            var cluster = new Cluster
            {
                Id = clusterId,
                CircuitBreakerOptions = new CircuitBreakerOptions
                {
                    MaxConcurrentRequests = GetLabel(labels, "IslandGateway.Backend.CircuitBreaker.MaxConcurrentRequests", DefaultCircuitbreakerMaxConcurrentRequests),
                    MaxConcurrentRetries = GetLabel(labels, "IslandGateway.Backend.CircuitBreaker.MaxConcurrentRetries", DefaultCircuitbreakerMaxConcurrentRequests),
                },
                QuotaOptions = new QuotaOptions
                {
                    Average = GetLabel(labels, "IslandGateway.Backend.Quota.Average", DefaultQuotaAverage),
                    Burst = GetLabel(labels, "IslandGateway.Backend.Quota.Burst", DefaultQuotaBurst),
                },
                PartitioningOptions = new ClusterPartitioningOptions
                {
                    PartitionCount = GetLabel(labels, "IslandGateway.Backend.Partitioning.Count", DefaultPartitionCount),
                    PartitionKeyExtractor = GetLabel(labels, "IslandGateway.Backend.Partitioning.KeyExtractor", DefaultPartitionKeyExtractor),
                    PartitioningAlgorithm = GetLabel(labels, "IslandGateway.Backend.Partitioning.Algorithm", DefaultPartitioningAlgorithm),
                },
                LoadBalancing = new LoadBalancingOptions(), // TODO
                HealthCheckOptions = new HealthCheckOptions
                {
                    Enabled = GetLabel(labels, "IslandGateway.Backend.Healthcheck.Enabled", false),
                    Interval = GetLabel<TimeSpanIso8601>(labels, "IslandGateway.Backend.Healthcheck.Interval", TimeSpan.Zero),
                    Timeout = GetLabel<TimeSpanIso8601>(labels, "IslandGateway.Backend.Healthcheck.Timeout", TimeSpan.Zero),
                    Port = GetLabel<int?>(labels, "IslandGateway.Backend.Healthcheck.Port", null),
                    Path = GetLabel<string>(labels, "IslandGateway.Backend.Healthcheck.Path", null),
                },
                Metadata = clusterMetadata,
            };
            return cluster;
        }

        private static string GetClusterId(Uri serviceName, Dictionary<string, string> labels)
        {
            if (!labels.TryGetValue("IslandGateway.Backend.BackendId", out var backendId) ||
                string.IsNullOrEmpty(backendId))
            {
                backendId = serviceName.ToString();
            }

            return backendId;
        }
    }
}