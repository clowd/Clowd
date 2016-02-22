using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Server
{
    public class ModelTypes
    {
        public enum SubscriptionType
        {
            Free  = 1,
            Pro = 2,
            Lifetime = 3
        }

        public enum AzureContainer
        {
            Private = 1,
            Public = 2,
        }
    }
}
