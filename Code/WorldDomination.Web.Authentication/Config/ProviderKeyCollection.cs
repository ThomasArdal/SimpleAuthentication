﻿using System.Configuration;

namespace WorldDomination.Web.Authentication.Config
{
    public class ProviderKeyCollection : KeyValueConfigurationCollection
    {
        public ProviderKey this[ProviderType provider]
        {
            get { return BaseGet(provider) as ProviderKey; }
            set
            {
                if (BaseGet(provider) != null)
                {
                    int index = BaseIndexOf(BaseGet(provider));
                    BaseRemoveAt(index);
                }
                BaseAdd(0, value);
            }
        }

        protected override ConfigurationElement CreateNewElement()
        {
            return new ProviderKey();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((ProviderKey) element).Name;
        }
    }
}