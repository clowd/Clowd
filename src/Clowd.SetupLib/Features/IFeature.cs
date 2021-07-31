﻿namespace Clowd.Setup.Features
{
    public interface IFeature
    {
        bool NeedsPrivileges();
        bool CheckInstalled(string assetPath);
        void Install(string assetPath);
        void Uninstall(string assetPath);
    }
}
