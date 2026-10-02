// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.UART
{
    public static class PrimeElectricalPorts
    {
        // Our NuttX board's A-F pin contract. These devices use the existing
        // UART connector, GPIO registers and arena-readable LPF2 interfaces.
        // Call once on a new paused machine; no firmware or pin modes change.
        public static void CreatePrimeElectricalPorts(this Emulation emulation, string machineName, string prefix = "port")
        {
            IMachine machine;
            if(!emulation.TryGetMachineByName(machineName, out machine))
                throw new RecoverableException("Prime machine was not found");
            if(string.IsNullOrEmpty(prefix)) throw new RecoverableException("Port prefix must not be empty");
            var definitions = new[] {
                new Definition("A", "uart7", "D",7,"D",8,"E",8,"E",7,"motor",9,11,1,2),
                new Definition("B", "uart4", "D",9,"D",10,"D",1,"D",0,"motor",13,14,3,4),
                new Definition("C", "uart8", "D",11,"E",4,"E",1,"E",0,"color",6,7,1,2,"B","B","timer4",2,false),
                new Definition("D", "uart5", "C",15,"C",14,"C",12,"D",2,"ultrasonic",8,9,3,4,"B","B","timer4",2,false),
                new Definition("E", "uart10", "C",13,"E",12,"E",3,"E",2,"force",6,7,1,2,"C","C","timer3",2,false),
                new Definition("F", "uart9", "C",11,"E",6,"D",15,"D",14,"none",8,1,3,4,"C","B","timer3",2,false)
            };
            var ports = new LegoLpf2ElectricalPort[definitions.Length];
            var uarts = new IUART[definitions.Length];
            var gpios = new STM32_GPIOPort[definitions.Length][];
            var timers=new STM32_Timer[definitions.Length];
            // Resolve every dependency and name before installing anything.
            for(var i=0;i<definitions.Length;i++)
            {
                var d=definitions[i]; object existing;
                if(emulation.ExternalsManager.TryGetByName(prefix+d.Name, out existing)
                    || emulation.ExternalsManager.TryGetByName(prefix+d.Name+"Wire", out existing))
                    throw new RecoverableException("Prime port name is already in use");
                if(!machine.TryGetByName("sysbus."+d.Uart, out uarts[i]))
                    throw new RecoverableException("Prime UART was not found");
                if(!machine.TryGetByName("sysbus."+d.TimerName, out timers[i]))
                    throw new RecoverableException("Prime motor timer was not found");
                gpios[i]=new STM32_GPIOPort[6];
                var banks=new[]{d.Id1Bank,d.Id2Bank,d.TxBank,d.RxBank,d.BridgeBank,d.BridgeBank2};
                for(var j=0;j<banks.Length;j++)
                    if(!machine.TryGetByName("sysbus.gpioPort"+banks[j], out gpios[i][j]))
                        throw new RecoverableException("Prime GPIO bank was not found");
            }
            for(var i=0;i<definitions.Length;i++)
            {
                var d=definitions[i];var g=gpios[i];
                ports[i]=new LegoLpf2ElectricalPort(machine,g[0],d.Id1,g[1],d.Id2,g[2],d.Tx,g[3],d.Rx,d.Device,
                    g[4],d.Bridge1,d.Bridge2,timers[i],d.Channel1,d.Channel2,
                    d.AlternateFunction,d.AdvancedTimer,g[5]);
                machine.RegisterAsAChildOf(uarts[i],ports[i],NullRegistrationPoint.Instance);
                machine.SetLocalName(ports[i],prefix+d.Name);
                var wire=new UARTHub<byte>(false);
                emulation.ExternalsManager.AddExternal(ports[i],prefix+d.Name);
                emulation.ExternalsManager.AddExternal(wire,prefix+d.Name+"Wire");
                emulation.Connector.Connect(uarts[i],wire);
                emulation.Connector.Connect(ports[i],wire);
            }
        }
        private sealed class Definition
        {
            public Definition(string name,string uart,string id1Bank,int id1,string id2Bank,int id2,
                string txBank,int tx,string rxBank,int rx,string device,int bridge1=0,int bridge2=0,int channel1=1,int channel2=2,
                string bridgeBank="E",string bridgeBank2="E",string timerName="timer1",int alternateFunction=1,bool advancedTimer=true)
            { Name=name;Uart=uart;Id1Bank=id1Bank;Id1=id1;Id2Bank=id2Bank;Id2=id2;TxBank=txBank;Tx=tx;
              RxBank=rxBank;Rx=rx;Device=device;Bridge1=bridge1;Bridge2=bridge2;Channel1=channel1;Channel2=channel2;
              BridgeBank=bridgeBank;BridgeBank2=bridgeBank2;TimerName=timerName;AlternateFunction=alternateFunction;AdvancedTimer=advancedTimer; }
            public readonly string Name,Uart,Id1Bank,Id2Bank,TxBank,RxBank,Device,BridgeBank,BridgeBank2,TimerName;
            public readonly int Id1,Id2,Tx,Rx,Bridge1,Bridge2,Channel1,Channel2,AlternateFunction;
            public readonly bool AdvancedTimer;
        }
    }
}
