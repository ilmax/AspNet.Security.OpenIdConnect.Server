using System;
using System.Collections.Generic;
using System.IO;
using Nancy.Bootstrapper;
using Nancy.Security;
using Nancy.TinyIoc;
using Nancy.ViewEngines.Razor;

namespace Nancy.Server
{
    public class NancyBootstrapper : DefaultNancyBootstrapper
    {
        protected override void ApplicationStartup(TinyIoCContainer container, IPipelines pipelines)
        {
            // Enable CSRF protection at the pipeline level
            // to be able to use it in AuthorizationModule.cs
            Csrf.Enable(pipelines);

            base.ApplicationStartup(container, pipelines);
        }

        protected override IEnumerable<Type> ViewEngines
        {
            get
            {
                yield return typeof(RazorViewEngine);
            }
        }

        protected override IRootPathProvider RootPathProvider => new NancyRootPathProvider();
    }

    public class NancyRootPathProvider : IRootPathProvider
    {
        public string GetRootPath() => Path.GetDirectoryName(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
    }

    public class RazorViewEngineRegistrations : Registrations
    {
        public RazorViewEngineRegistrations()
        {
            RegisterWithDefault<IRazorConfiguration>(typeof(DefaultRazorConfiguration));
        }
    }
}