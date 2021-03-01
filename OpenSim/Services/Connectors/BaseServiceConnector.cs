using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;

using Nini.Config;

namespace OpenSim.Services.Connectors
{
    public class BaseServiceConnector
    {
        protected IServiceAuth _Auth;

        public BaseServiceConnector() { }

        public BaseServiceConnector(IConfigSource config, string section)
        {
            Initialise(config, section);
        }

        public void Initialise(IConfigSource config, string section)
        {
            string authType = Util.GetConfigVarFromSections<string>(config, "AuthType", new string[] { "Network", section }, "None");

            switch (authType)
            {
                case "BasicHttpAuthentication":
                    _Auth = new BasicHttpAuthentication(config, section);
                    break;
            }

        }
    }
}
