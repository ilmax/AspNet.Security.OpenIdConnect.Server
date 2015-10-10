using System;
using System.Collections.Generic;
using System.IO;
using Nancy.Bootstrapper;
using Nancy.ViewEngines.Razor;

namespace Nancy.Client
{
    public class NancyBootstrapper : DefaultNancyBootstrapper
    {
        protected override IRootPathProvider RootPathProvider => new NancyRootPathProvider();

        //protected override DiagnosticsConfiguration DiagnosticsConfiguration => new DiagnosticsConfiguration {Password = @"secret"};

        protected override IEnumerable<Type> ViewEngines
        {
            get { yield return typeof (RazorViewEngine); }
        }
    }

    public class NancyRootPathProvider : IRootPathProvider
    {
        public string GetRootPath() => Path.GetDirectoryName(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
    }

    public class RazorViewEngineRegistrations : Registrations
    {
        public RazorViewEngineRegistrations()
        {
            RegisterWithDefault<IRazorConfiguration>(typeof (DefaultRazorConfiguration));
        }
    }
}