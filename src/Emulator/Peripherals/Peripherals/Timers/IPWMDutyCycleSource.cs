// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using Antmicro.Renode.Peripherals;

namespace Antmicro.Renode.Peripherals.Timers
{
    public interface IPWMDutyCycleSource : IPeripheral
    {
        double GetDutyCycle(int channel);
        bool Operational { get; }
        string UnsupportedConfiguration { get; }
    }
}
