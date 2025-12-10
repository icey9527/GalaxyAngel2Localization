using System.Collections.Generic;

namespace GalaxyAngel2Localization.Workspace
{

    public sealed class DatIndex
    {
        public List<uint> Tab1 { get; set; } = new();
        public List<string> Tab2 { get; set; } = new();
        public Dictionary<string, List<string>> Tab3 { get; set; } = new();
    }
}