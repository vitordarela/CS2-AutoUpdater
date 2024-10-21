using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoUpdater
{
    public class Container
    {
        public string container_name { get; set; }

        public string app_version { get; set; }

        public DateTime creation_date { get; set; }

        public DateTime last_update { get; set; }

        public bool updated { get; set; }
    }
}
