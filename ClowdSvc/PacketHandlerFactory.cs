using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Clowd.Shared;

namespace ClowdSvc
{
    public class PacketHandlerFactory
    {
        public delegate object HandlerMethodDelegate(object target, object[] args);

        public static Action<Packet> InitializeHandlerForObject<T>(T instance)
        {
            var handlers = new List<PacketHandlerIntermediate>();
            HandlerMethodDelegate catchall = null;
            var type = instance.GetType();
            var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (MethodInfo method in methods)
            {
                var attributes = method.GetCustomAttributes(true);
                foreach (Attribute attr in attributes)
                {
                    if (attr is PacketHandlerAttribute)
                    {
                        PacketHandlerIntermediate phi = new PacketHandlerIntermediate(attr as PacketHandlerAttribute, method);
                        handlers.Add(phi);
                    }
                    else if (attr is PacketCatchAllAttribute)
                    {
                        catchall = GenerateDelegate(method);
                    }
                }
            }
            Console.WriteLine();
            return new Action<Packet>((obj) =>
            {
                string command = obj.Command;
                var hndlrs = handlers.Where(phi => phi.Command.Equals(obj.Command, StringComparison.InvariantCultureIgnoreCase)).ToArray();
                if (!hndlrs.Any() && catchall != null)
                {
                    object[] args = { obj };
                    catchall(instance, args);
                }
                else
                {
                    foreach (var hn in hndlrs)
                    {
                        hn.Call(instance, obj);
                    }
                }
            });
        }

        private static HandlerMethodDelegate GenerateDelegate(MethodInfo method)
        {
            ParameterInfo[] parms = method.GetParameters();
            int numberOfParameters = parms.Length;
            Type[] args = { typeof(object), typeof(object[]) };
            DynamicMethod dynam = new DynamicMethod(String.Empty, typeof(object), args, typeof(PacketHandlerFactory));
            ILGenerator il = dynam.GetILGenerator();
            Label argsOk = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Ldc_I4, numberOfParameters);
            il.Emit(OpCodes.Beq, argsOk);
            il.Emit(OpCodes.Newobj, typeof(TargetParameterCountException).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Throw);
            il.MarkLabel(argsOk);
            if (!method.IsStatic)
            {
                il.Emit(OpCodes.Ldarg_0);
            }
            int i = 0;
            while (i < numberOfParameters)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                Type parmType = parms[i].ParameterType;
                if (parmType.IsValueType)
                {
                    il.Emit(OpCodes.Unbox_Any, parmType);
                }
                i++;
            }
            il.Emit(method.IsFinal ? OpCodes.Call : OpCodes.Callvirt, method);
            if (method.ReturnType != typeof(void))
            {
                if (method.ReturnType.IsValueType)
                {
                    il.Emit(OpCodes.Box, method.ReturnType);
                }
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }
            il.Emit(OpCodes.Ret);
            return (HandlerMethodDelegate)dynam.CreateDelegate(typeof(HandlerMethodDelegate));
        }

        [AttributeUsage(AttributeTargets.Method)]
        public class PacketHandlerAttribute : System.Attribute
        {
            public string Command { get; private set; }
            public bool CallOnce { get; private set; }
            public PacketHandlerAttribute(string command)
            {
                this.Command = command;
                this.CallOnce = false;
            }
            public PacketHandlerAttribute(string command, bool callOnce)
            {
                this.Command = command;
                this.CallOnce = callOnce;
            }
        }
        [AttributeUsage(AttributeTargets.Method)]
        public class PacketCatchAllAttribute : System.Attribute
        {
            public PacketCatchAllAttribute()
            {
            }
        }

        public class PacketHandlerIntermediate
        {
            public string Command { get; private set; }
            public bool CallOnce { get; private set; }
            public bool Called { get; private set; }

            private readonly HandlerMethodDelegate _methodDelegate;

            public PacketHandlerIntermediate(PacketHandlerAttribute attribute, MethodInfo method)
            {
                this.Called = false;
                this.Command = attribute.Command;
                this.CallOnce = attribute.CallOnce;
                this._methodDelegate = GenerateDelegate(method);
            }

            public void Call(object sender, Packet p)
            {
                if (Called && CallOnce) return;
                Called = true;
                object[] args = { p };
                this._methodDelegate(sender, args);
            }
        }
    }
}
