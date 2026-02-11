using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EliteDangerousStationManager.Services
{
    internal class ServiceLocator
    {
        private static readonly Lazy<ProjectDatabaseService> _serverDb =
        new(() => new ProjectDatabaseService("Server"));

        public static ProjectDatabaseService ServerDb => _serverDb.Value;
    }
}
