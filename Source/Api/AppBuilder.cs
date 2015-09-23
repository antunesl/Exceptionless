﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Cors;
using System.Web.Http;
using System.Web.Http.ExceptionHandling;
using System.Web.Http.Routing;
using AutoMapper;
using Exceptionless.Api.Extensions;
using Exceptionless.Api.Security;
using Exceptionless.Api.Utility;
using Exceptionless.Core;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Jobs;
using Exceptionless.Core.Models;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Utility;
using Exceptionless.Serializer;
using Foundatio.Jobs;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Infrastructure;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Nito.AsyncEx;
using NLog.Fluent;
using Owin;
using SimpleInjector;
using SimpleInjector.Integration.WebApi;
using Swashbuckle.Application;

namespace Exceptionless.Api {
    public static class AppBuilder {
        public static void Build(IAppBuilder app) {
            BuildWithContainer(app, CreateContainer());
        }

        public static void BuildWithContainer(IAppBuilder app, Container container) {
            if (container == null)
                throw new ArgumentNullException(nameof(container));

            var contractResolver = container.GetInstance<IContractResolver>();
            var exceptionlessContractResolver = contractResolver as ExceptionlessContractResolver;
            exceptionlessContractResolver?.UseDefaultResolverFor(typeof(Connection).Assembly);

            Config = new HttpConfiguration();
            Config.DependencyResolver = new SimpleInjectorWebApiDependencyResolver(container);
            Config.Formatters.Remove(Config.Formatters.XmlFormatter);
            Config.Formatters.JsonFormatter.SerializerSettings.Formatting = Formatting.Indented;
            Config.Formatters.JsonFormatter.SerializerSettings.ContractResolver = contractResolver;

            SetupRouteConstraints(Config);
            container.RegisterWebApiControllers(Config);

            VerifyContainer(container);

            container.Bootstrap(Config);
            container.Bootstrap(app);

            Log.Info().Message("Starting api...").Write();
            Config.Services.Add(typeof(IExceptionLogger), new NLogExceptionLogger());
            Config.Services.Replace(typeof(IExceptionHandler), container.GetInstance<ExceptionlessReferenceIdExceptionHandler>());

            Config.MessageHandlers.Add(container.GetInstance<XHttpMethodOverrideDelegatingHandler>());
            Config.MessageHandlers.Add(container.GetInstance<EncodingDelegatingHandler>());
            Config.MessageHandlers.Add(container.GetInstance<AuthMessageHandler>());

            // Throttle api calls to X every 15 minutes by IP address.
            Config.MessageHandlers.Add(container.GetInstance<ThrottlingHandler>());

            // Reject event posts in orgs over their max event limits.
            Config.MessageHandlers.Add(container.GetInstance<OverageHandler>());

            app.UseCors(new CorsOptions {
                PolicyProvider = new CorsPolicyProvider {
                    PolicyResolver = ctx => Task.FromResult(new CorsPolicy {
                        AllowAnyHeader = true,
                        AllowAnyMethod = true,
                        AllowAnyOrigin = true,
                        SupportsCredentials = true,
                        PreflightMaxAge = 60 * 5
                    })
                }
            });

            app.CreatePerContext<AsyncLazy<User>>("User", ctx => Task.FromResult(new AsyncLazy<User>(async () => {
                if (ctx.Request.User?.Identity == null || !ctx.Request.User.Identity.IsAuthenticated)
                    return null;

                string userId = ctx.Request.User.GetUserId();
                if (String.IsNullOrEmpty(userId))
                    return null;

                var userRepository = container.GetInstance<IUserRepository>();
                return await userRepository.GetByIdAsync(userId, true);
            })));

            app.CreatePerContext<AsyncLazy<Project>>("DefaultProject", ctx => Task.FromResult(new AsyncLazy<Project>(async () => {
                if (ctx.Request.User?.Identity == null || !ctx.Request.User.Identity.IsAuthenticated)
                    return null;

                // TODO: Use project id from url. E.G., /projects/{projectId:objectid}/events
                string projectId = ctx.Request.User.GetDefaultProjectId();
                var projectRepository = container.GetInstance<IProjectRepository>();

                if (String.IsNullOrEmpty(projectId)) {
                    var firstOrgId = ctx.Request.User.GetOrganizationIds().FirstOrDefault();
                    if (!String.IsNullOrEmpty(firstOrgId)) {
                        var project = (await projectRepository.GetByOrganizationIdAsync(firstOrgId, useCache: true)).Documents.FirstOrDefault();
                        if (project != null)
                            return project;
                    }

                    if (Settings.Current.WebsiteMode == WebsiteMode.Dev) {
                        var dataHelper = container.GetInstance<DataHelper>();
                        // create a default org and project
                        projectId = await dataHelper.CreateDefaultOrganizationAndProjectAsync(await ctx.Request.GetUserAsync());
                    }
                }

                if (String.IsNullOrEmpty(projectId))
                    return null;

                return await projectRepository.GetByIdAsync(projectId, true);
            })));

            app.UseWebApi(Config);
            var resolver = new SimpleInjectorSignalRDependencyResolver(container);
            if (Settings.Current.EnableRedis)
                resolver.UseRedis(new RedisScaleoutConfiguration(Settings.Current.RedisConnectionString, "exceptionless.signalr"));
            app.MapSignalR("/api/v2/push", new HubConfiguration { Resolver = resolver });

            SetupSwagger(Config);

            Mapper.Configuration.ConstructServicesUsing(container.GetInstance);
            if (Settings.Current.WebsiteMode != WebsiteMode.Dev)
                Task.Run(async () => await CreateSampleDataAsync(container));

            if (Settings.Current.RunJobsInProcess) {
                Log.Warn().Message("Jobs running in process.").Write();

                var context = new OwinContext(app.Properties);
                var token = context.Get<CancellationToken>("host.OnAppDisposing");
                JobRunner.RunContinuousAsync<EventPostsJob>(cancellationToken: token);
                JobRunner.RunContinuousAsync<EventUserDescriptionsJob>(cancellationToken: token);
                JobRunner.RunContinuousAsync<MailMessageJob>(cancellationToken: token);
                JobRunner.RunContinuousAsync<EventNotificationsJob>(cancellationToken: token);
                JobRunner.RunContinuousAsync<WebHooksJob>(cancellationToken: token);
                JobRunner.RunContinuousAsync<DailySummaryJob>(cancellationToken: token, interval: TimeSpan.FromHours(1));
                JobRunner.RunContinuousAsync<DownloadGeoIPDatabaseJob>(cancellationToken: token, interval: TimeSpan.FromDays(1));
                JobRunner.RunContinuousAsync<RetentionLimitsJob>(cancellationToken: token, interval: TimeSpan.FromDays(1));
            
                JobRunner.RunContinuousAsync<WorkItemJob>(instanceCount: 2, cancellationToken: token);
            } else {
                Log.Info().Message("Jobs running out of process.").Write();
            }
        }

        private static void SetupRouteConstraints(HttpConfiguration config) {
            var constraintResolver = new DefaultInlineConstraintResolver();
            constraintResolver.ConstraintMap.Add("objectid", typeof(ObjectIdRouteConstraint));
            constraintResolver.ConstraintMap.Add("objectids", typeof(ObjectIdsRouteConstraint));
            constraintResolver.ConstraintMap.Add("token", typeof(TokenRouteConstraint));
            constraintResolver.ConstraintMap.Add("tokens", typeof(TokensRouteConstraint));
            config.MapHttpAttributeRoutes(constraintResolver);
        }

        private static void SetupSwagger(HttpConfiguration config) {
            config.EnableSwagger("schema/{apiVersion}", c => {
                c.SingleApiVersion("v2", "Exceptionless");
                c.ApiKey("access_token").In("header").Name("access_token").Description("API Key Authentication");
                c.BasicAuth("basic").Description("Basic HTTP Authentication");
                c.IncludeXmlComments($@"{AppDomain.CurrentDomain.BaseDirectory}\bin\Exceptionless.Api.xml");
                c.IgnoreObsoleteActions();
                c.DocumentFilter<FilterRoutesDocumentFilter>();
            }).EnableSwaggerUi("docs/{*assetPath}", c => {
                c.InjectStylesheet(typeof(AppBuilder).Assembly, "Exceptionless.Api.Content.docs.css");
                c.InjectJavaScript(typeof(AppBuilder).Assembly, "Exceptionless.Api.Content.docs.js");
            });
        }

        private static async Task CreateSampleDataAsync(Container container) {
            if (Settings.Current.WebsiteMode != WebsiteMode.Dev)
                return;

            var userRepository = container.GetInstance<IUserRepository>();
            if (await userRepository.CountAsync() != 0)
                return;

            var dataHelper = container.GetInstance<DataHelper>();
            await dataHelper.CreateTestDataAsync();
        }

        public static Container CreateContainer(bool includeInsulation = true) {
            var container = new Container();
            container.Options.AllowOverridingRegistrations = true;
            container.Options.DefaultScopedLifestyle = new WebApiRequestLifestyle();
            container.Options.PropertySelectionBehavior = new InjectAttributePropertySelectionBehavior();
            container.Options.ResolveUnregisteredCollections = true;

            container.RegisterPackage<Core.Bootstrapper>();
            container.RegisterPackage<Bootstrapper>();

            if (!includeInsulation)
                return container;

            Assembly insulationAssembly = null;
            try {
                insulationAssembly = Assembly.Load("Exceptionless.Insulation");
            } catch (Exception ex) {
                Log.Error().Message("Unable to load the insulation assembly.").Exception(ex).Write();
            }

            if (insulationAssembly != null)
                container.RegisterPackages(new[] { insulationAssembly });

            return container;
        }

        private static void VerifyContainer(Container container) {
            try {
                container.Verify();
            } catch (Exception ex) {
                var tempEx = ex;
                while (!(tempEx is ReflectionTypeLoadException)) {
                    if (tempEx.InnerException == null)
                        break;
                    tempEx = tempEx.InnerException;
                }

                var typeLoadException = tempEx as ReflectionTypeLoadException;
                if (typeLoadException != null) {
                    foreach (var loaderEx in typeLoadException.LoaderExceptions)
                        Debug.WriteLine(loaderEx.Message);
                }

                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public static HttpConfiguration Config { get; private set; }
    }
}