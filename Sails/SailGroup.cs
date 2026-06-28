using System.Collections.Generic;
using System.Linq;

namespace SailwindVirtualCrew
{
    public class SailGroup
    {
        public const string AllSailsId = "all-sails";

        public string Id { get; }
        public string Name { get; set; }
        public bool IsAllSails { get; }

        private readonly HashSet<string> memberIdentifiers = new HashSet<string>();
        public IReadOnlyCollection<string> MemberIdentifiers => memberIdentifiers;

        public SailGroup(string name, bool isAllSails = false, string id = null)
        {
            Id = isAllSails ? AllSailsId : string.IsNullOrEmpty(id) ? System.Guid.NewGuid().ToString("N") : id;
            Name = name;
            IsAllSails = isAllSails;
        }

        public IEnumerable<ICommonSailActions> GetMembers(IReadOnlyList<ICommonSailActions> allSails)
        {
            if (IsAllSails) return allSails;
            return allSails.Where(s => memberIdentifiers.Contains(s.getDefaultIdentifier()));
        }

        public bool Contains(ICommonSailActions sail) =>
            IsAllSails || memberIdentifiers.Contains(sail.getDefaultIdentifier());

        public void AddSail(ICommonSailActions sail) =>
            memberIdentifiers.Add(sail.getDefaultIdentifier());

        public void RemoveSail(ICommonSailActions sail) =>
            memberIdentifiers.Remove(sail.getDefaultIdentifier());

        public void AddIdentifier(string id) =>
            memberIdentifiers.Add(id);

        public SailCapability GetCommonCapabilities(IReadOnlyList<ICommonSailActions> allSails)
        {
            var members = GetMembers(allSails).Where(s => s.getRealSail() != null).ToList();
            if (members.Count == 0) return SailCapability.None;
            var result = SailCapability.All;
            foreach (var sail in members)
                result &= sail.GetCapabilities();
            return result;
        }
    }
}
