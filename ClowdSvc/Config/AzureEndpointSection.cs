using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Server.Config
{
    public class AzureEndpointSection : ConfigurationSection
    {
        [ConfigurationProperty("", IsRequired = true, IsDefaultCollection = true)]
        public AzureEndpointInstanceCollection Instances
        {
            get { return (AzureEndpointInstanceCollection)this[""]; }
            set { this[""] = value; }
        }
    }
    public class AzureEndpointInstanceCollection : ConfigurationElementCollection
    {
        protected override ConfigurationElement CreateNewElement()
        {
            return new AzureEndpointElement();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            //set to whatever Element Property you want to use for a key
            return ((AzureEndpointElement)element).Name;
        }
        public new AzureEndpointElement this[string elementName]
        {
            get
            {
                return this.OfType<AzureEndpointElement>().FirstOrDefault(item => item.Name == elementName);
            }
        }
    }

    public class AzureEndpointElement : ConfigurationElement
    {
        [ConfigurationProperty("name", IsKey = true, IsRequired = true)]
        public string Name
        {
            get { return (string)base["name"]; }
            set { base["name"] = value; }
        }

        [ConfigurationProperty("connectionStringName", IsRequired = true)]
        public string ConnectionStringName
        {
            get { return (string)base["connectionStringName"]; }
            set { base["connectionStringName"] = value; }
        }

        [ConfigurationProperty("endpoint", IsRequired = true)]
        public string Endpoint
        {
            get { return (string)base["endpoint"]; }
            set { base["endpoint"] = value; }
        }
    }
}