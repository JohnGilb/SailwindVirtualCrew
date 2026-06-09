using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SailwindVirtualCrew
{
    public interface ICommonSailActions
    {
        string getSailName();
        string getDefaultIdentifier();
        string FriendlyName { get; set; }
        Sail getRealSail();
        GPButtonRopeWinch getHalyardWinch();
        void stop();
        void deploySail();
        void reefSail();
        void easeSail();
        void trimSail();
    }
}
