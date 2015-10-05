using Microsoft.Owin.Hosting;
using System;

namespace Nancy.Server
{
    public class Program
    {
        public void Main(string[] args)
        {
            string baseAddress = "http://localhost:54541/";

            // Start OWIN host 
            using (WebApp.Start<Startup>(url: baseAddress))
            {
                Console.WriteLine("Press CTRL+C to stop the client");
                Console.ReadLine();
            }
        }
    }
}
