// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ReverseProxy.Abstractions;
using Microsoft.ReverseProxy.Service.Proxy.Infrastructure;

namespace YARP.Sample
{
    /// <summary>
    /// Initialiaztion for ASP.NET using YARP reverse proxy
    /// </summary>
    public class Startup
    {
        /// <summary>
        /// This method gets called by the runtime. Use this method to add services to the container.
        /// </summary>
        public void ConfigureServices(IServiceCollection services)
        {
            // Specify a custom proxy config provider, in this case defined in InMemoryConfigProvider.cs
            // Programatically creating route and cluster configs. This allows loading the data from an arbitrary source.
            services.AddReverseProxy()
                .LoadFromMemory(GetRoutes(), GetClusters());

            services.AddHttpContextAccessor();
            services.AddSingleton<IProxyHttpClientFactory, DynamicDestinationsProxyClientFactory>();
        }

        /// <summary>
        /// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        /// </summary>
        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                // We can customize the proxy pipeline and add/remove/replace steps
                endpoints.MapReverseProxy();
            });
        }

        private ProxyRoute[] GetRoutes()
        {
            return new[]
            {
                new ProxyRoute()
                {
                    RouteId = "virtual-route-1",
                    ClusterId = "virtual-cluster-1",
                    Match = new ProxyMatch { Path = "/{appName}/{svcName}/{**catch-all}" }
                }
            };
        }
        private Cluster[] GetClusters()
        {
            return new[]
            {
                new Cluster
                {
                    Id = "virtual-cluster-1",
                    SessionAffinity = new SessionAffinityOptions { Enabled = true, Mode = "Cookie" },
                    Destinations = new Dictionary<string, Destination>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "virtual-destination-1", new Destination { Address = "https://127.0.0.1" } },
                    },
                }
            };
        }

        private class DynamicDestinationsProxyClientFactory : IProxyHttpClientFactory
        {
            private readonly IHttpContextAccessor _httpContextAccessor;

            public DynamicDestinationsProxyClientFactory(IHttpContextAccessor httpContextAccessor)
            {
                _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            }

            public HttpMessageInvoker CreateClient(ProxyHttpClientContext context)
            {
                var inner = new SocketsHttpHandler();

                var dynamicResolutionHandler = new DynamicResolutionHandler(_httpContextAccessor);
                dynamicResolutionHandler.InnerHandler = inner;

                return new HttpMessageInvoker(dynamicResolutionHandler);
            }
        }

        private class DynamicResolutionHandler : DelegatingHandler
        {
            private readonly IHttpContextAccessor _httpContextAccessor;

            public DynamicResolutionHandler(IHttpContextAccessor httpContextAccessor)
            {
                _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var httpContext = _httpContextAccessor.HttpContext;
                var routeData = httpContext.GetRouteData();

                var appName = routeData.Values["appName"] as string;
                var svcName = routeData.Values["svcName"] as string;

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content = new StringContent($"Dynamically resolved and proxied to 'fabric:/{appName}/{svcName}' (no, not really, but you get the gist)");

                return Task.FromResult(response);
            }
        }
    }
}
