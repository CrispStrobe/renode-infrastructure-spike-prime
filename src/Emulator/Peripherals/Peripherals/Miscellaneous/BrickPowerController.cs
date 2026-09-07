//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Board-level, deterministic battery/charger/power-latch observer.
    public class BrickPowerController : IPeripheral, IGPIOReceiver, INumberedGPIOOutput
    {
        public BrickPowerController(int initialBatteryMillivolts = 7400)
        {
            Connections = new Dictionary<int, IGPIO> { { 0, new GPIO() }, { 1, new GPIO() } };
            SetBatteryMillivolts(initialBatteryMillivolts);
            Reset();
        }

        public void Reset()
        {
            PowerHold = false;
            ChargerMode = false;
            ShutdownRequested = false;
            UpdateOutputs();
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == PowerHoldInput)
            {
                if(PowerHold && !value)
                {
                    ShutdownRequested = true;
                }
                PowerHold = value;
            }
            else if(number == ChargerModeInput)
            {
                ChargerMode = value;
            }
        }

        public void SetBatteryMillivolts(int value)
        {
            if(value < MinimumBatteryMillivolts || value > MaximumBatteryMillivolts)
            {
                throw new System.ArgumentOutOfRangeException(nameof(value));
            }
            BatteryMillivolts = value;
            UpdateOutputs();
        }

        public void SetChargerConnected(bool value)
        {
            ChargerConnected = value;
            UpdateOutputs();
        }

        public void ClearShutdownRequest()
        {
            ShutdownRequested = false;
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }
        public int BatteryMillivolts { get; private set; }
        public bool ChargerConnected { get; private set; }
        public bool PowerHold { get; private set; }
        public bool ChargerMode { get; private set; }
        public bool ShutdownRequested { get; private set; }
        public bool BatteryLow => BatteryMillivolts < LowBatteryThresholdMillivolts;

        private void UpdateOutputs()
        {
            Connections[PowerGoodOutput].Set(!BatteryLow || ChargerConnected);
            Connections[ChargeStatusOutput].Set(ChargerConnected);
        }

        public const int PowerHoldInput = 0;
        public const int ChargerModeInput = 1;
        public const int PowerGoodOutput = 0;
        public const int ChargeStatusOutput = 1;
        public const int LowBatteryThresholdMillivolts = 6000;
        public const int MinimumBatteryMillivolts = 0;
        public const int MaximumBatteryMillivolts = 20000;
    }
}
