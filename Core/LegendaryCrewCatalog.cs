using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SailwindVirtualCrew
{
    public sealed class LegendaryCrewDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public ShipRole Role { get; }
        public string HomePort { get; }
        public int ModelIndex { get; }

        internal LegendaryCrewDefinition(string id, string name, ShipRole role, string homePort, int modelIndex)
        {
            Id = id;
            Name = name;
            Role = role;
            HomePort = homePort;
            ModelIndex = modelIndex;
        }
    }

    internal static class LegendaryCrewCatalog
    {
        private const int LegendaryStat = 5;

        private static readonly List<LegendaryCrewDefinition> Definitions = new List<LegendaryCrewDefinition>
        {
            new LegendaryCrewDefinition("legendary-pytheas", "Pytheas", ShipRole.Navigator, "Chronos", 0),
            new LegendaryCrewDefinition("legendary-rodrigo-de-triana", "Rodrigo de Triana", ShipRole.Lookout, "Aestra Abbey", 1),
            new LegendaryCrewDefinition("legendary-francis-chichester", "Francis Chichester", ShipRole.Pilot, "Mirage Mountain", 2),
            new LegendaryCrewDefinition("legendary-horatio-nelson", "Horatio Nelson", ShipRole.ChiefOfficer, "Sen'na", 3),
            new LegendaryCrewDefinition("legendary-cornelis-de-houtman", "Cornelis de Houtman", ShipRole.Quartermaster, "Serpent Isle", 4),
            new LegendaryCrewDefinition("legendary-jan-huyghen-van-linschoten", "Jan Huyghen van Linschoten", ShipRole.Supercargo, "Oasis", 5),
            new LegendaryCrewDefinition("legendary-edward-barlow", "Edward Barlow", ShipRole.Deckhand, "Al'Nilem", 6)
        };

        internal static IReadOnlyList<LegendaryCrewDefinition> All { get; } =
            new ReadOnlyCollection<LegendaryCrewDefinition>(Definitions);

        internal static IEnumerable<LegendaryCrewDefinition> ForPort(string portName)
        {
            return Definitions.Where(d => IsPortMatch(d.HomePort, portName));
        }

        internal static bool TryGet(string id, out LegendaryCrewDefinition definition)
        {
            definition = Definitions.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            return definition != null;
        }

        internal static bool IsLegendaryId(string id)
        {
            return Definitions.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsPortMatch(string expectedPortName, string actualPortName)
        {
            return string.Equals(NormalizePortName(expectedPortName), NormalizePortName(actualPortName), StringComparison.OrdinalIgnoreCase);
        }

        internal static Crewman Create(LegendaryCrewDefinition definition)
        {
            return new Crewman(
                definition.Name,
                definition.Role,
                LegendaryStat, LegendaryStat, LegendaryStat, LegendaryStat, LegendaryStat, LegendaryStat,
                LegendaryStat, LegendaryStat, LegendaryStat, LegendaryStat, LegendaryStat, LegendaryStat,
                -1f,
                definition.Id,
                definition.ModelIndex,
                CrewShift.AdHoc);
        }

        private static string NormalizePortName(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                return string.Empty;

            return portName
                .Replace("'", string.Empty)
                .Replace("`", string.Empty)
                .Replace(" ", string.Empty)
                .Trim();
        }
    }
}
