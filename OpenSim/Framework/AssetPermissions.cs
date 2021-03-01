using System;
using System.Reflection;

using Nini.Config;
using log4net;

using OpenMetaverse;

namespace OpenSim.Framework
{
    public class AssetPermissions
    {
        private static readonly ILog _log =
            LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

        private readonly bool[] _DisallowExport;
        private readonly bool[] _DisallowImport;
        private readonly string[] _AssetTypeNames;

        public AssetPermissions(IConfig config)
        {
            Type enumType = typeof(AssetType);
            _AssetTypeNames = Enum.GetNames(enumType);
            for (int i = 0; i < _AssetTypeNames.Length; i++)
                _AssetTypeNames[i] = _AssetTypeNames[i].ToLower();
            int n = Enum.GetValues(enumType).Length;
            _DisallowExport = new bool[n];
            _DisallowImport = new bool[n];

            LoadPermsFromConfig(config, "DisallowExport", _DisallowExport);
            LoadPermsFromConfig(config, "DisallowImport", _DisallowImport);

        }

        private void LoadPermsFromConfig(IConfig assetConfig, string variable, bool[] bitArray)
        {
            if (assetConfig == null)
                return;

            string perms = assetConfig.GetString(variable, string.Empty);
            string[] parts = perms.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string s in parts)
            {
                int index = Array.IndexOf(_AssetTypeNames, s.Trim().ToLower());
                if (index >= 0)
                    bitArray[index] = true;
                else
                    _log.WarnFormat("[Asset Permissions]: Invalid AssetType {0}", s);
            }

        }

        public bool AllowedExport(sbyte type)
        {
            string assetTypeName = ((AssetType)type).ToString();

            int index = Array.IndexOf(_AssetTypeNames, assetTypeName.ToLower());
            if (index >= 0 && _DisallowExport[index])
            {
                _log.DebugFormat("[Asset Permissions]: Export denied: configuration does not allow export of AssetType {0}", assetTypeName);
                return false;
            }

            return true;
        }

        public bool AllowedImport(sbyte type)
        {
            string assetTypeName = ((AssetType)type).ToString();

            int index = Array.IndexOf(_AssetTypeNames, assetTypeName.ToLower());
            if (index >= 0 && _DisallowImport[index])
            {
                _log.DebugFormat("[Asset Permissions]: Import denied: configuration does not allow import of AssetType {0}", assetTypeName);
                return false;
            }

            return true;
        }


    }
}
