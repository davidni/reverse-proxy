// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.ReverseProxy.Core.Abstractions;
using Microsoft.ReverseProxy.Core.RuntimeModel;
using Microsoft.ReverseProxy.Utilities;

namespace Microsoft.ReverseProxy.Core.Service.Proxy
{
    /// <summary>
    /// Default implementation of <see cref="ILoadBalancer"/>.
    /// </summary>
    internal class LoadBalancer : ILoadBalancer
    {
        private readonly IRandom _random;

        public LoadBalancer(IRandomFactory randomFactory)
        {
            _random = randomFactory.CreateRandomInstance();
        }

        public EndpointInfo PickEndpoint(
            IReadOnlyList<EndpointInfo> healthyEndpoints,
            IReadOnlyList<EndpointInfo> allEndpoints,
            in BackendConfig.BackendLoadBalancingOptions loadBalancingOptions)
        {
            var endpointCount = healthyEndpoints.Count;
            if (endpointCount == 0)
            {
                return null;
            }

            if (endpointCount == 1)
            {
                return healthyEndpoints[0];
            }

            switch (loadBalancingOptions.Mode)
            {
                case LoadBalancingMode.First:
                    return healthyEndpoints[0];
                case LoadBalancingMode.Random:
                    return healthyEndpoints[_random.Next(endpointCount)];
                case LoadBalancingMode.PowerOfTwoChoices:
                    // Pick two, and then return the least busy. This avoids the effort of searching the whole list, but
                    // still avoids overloading a single endpoint.
                    var firstEndpoint = healthyEndpoints[_random.Next(endpointCount)];
                    var secondEndpoint = healthyEndpoints[_random.Next(endpointCount)];
                    return (firstEndpoint.ConcurrencyCounter.Value <= secondEndpoint.ConcurrencyCounter.Value) ? firstEndpoint : secondEndpoint;
                case LoadBalancingMode.LeastRequests:
                    var leastRequestsEndpoint = healthyEndpoints[0];
                    var leastRequestsCount = leastRequestsEndpoint.ConcurrencyCounter.Value;
                    for (var i = 1; i < endpointCount; i++)
                    {
                        var endpoint = healthyEndpoints[i];
                        var endpointRequestCount = endpoint.ConcurrencyCounter.Value;
                        if (endpointRequestCount < leastRequestsCount)
                        {
                            leastRequestsEndpoint = endpoint;
                            leastRequestsCount = endpointRequestCount;
                        }
                    }
                    return leastRequestsEndpoint;
                default:
                    throw new ReverseProxyException($"Load balancing mode '{loadBalancingOptions.Mode}' is not supported.");
            }
        }
    }
}
