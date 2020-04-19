// all the [TargetRpc] code from NetworkBehaviourProcessor in one place

using Mono.CecilX;
using Mono.CecilX.Cil;

namespace Mirror.Weaver
{
    public static class TargetRpcProcessor
    {
        private const string TargetRpcPrefix = "InvokeTargetRpc";

        // helper functions to check if the method has a NetworkConnection parameter
        public static bool HasNetworkConnectionParameter(MethodDefinition md)
        {
            return md.Parameters.Count > 0 &&
                   md.Parameters[0].ParameterType.FullName == Weaver.NetworkConnectionType.FullName;
        }

        public static MethodDefinition ProcessTargetRpcInvoke(TypeDefinition td, MethodDefinition md,
            MethodDefinition rpcCallFunc)
        {
            var rpc = new MethodDefinition(RpcProcessor.RpcPrefix + md.Name, MethodAttributes.Family |
                                                                             MethodAttributes.Static |
                                                                             MethodAttributes.HideBySig,
                Weaver.voidType);

            var rpcWorker = rpc.Body.GetILProcessor();
            var label = rpcWorker.Create(OpCodes.Nop);

            NetworkBehaviourProcessor.WriteClientActiveCheck(rpcWorker, md.Name, label, "TargetRPC");

            // setup for reader
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldarg_0));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Castclass, td));

            // NetworkConnection parameter is optional
            var hasNetworkConnection = HasNetworkConnectionParameter(md);
            if (hasNetworkConnection)
                //ClientScene.readyconnection
                rpcWorker.Append(rpcWorker.Create(OpCodes.Call, Weaver.ReadyConnectionReference));

            // process reader parameters and skip first one if first one is NetworkConnection
            if (!NetworkBehaviourProcessor.ProcessNetworkReaderParameters(md, rpcWorker, hasNetworkConnection))
                return null;

            // invoke actual command function
            rpcWorker.Append(rpcWorker.Create(OpCodes.Callvirt, rpcCallFunc));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ret));

            NetworkBehaviourProcessor.AddInvokeParameters(rpc.Parameters);

            return rpc;
        }

        /* generates code like:
            public void TargetTest (NetworkConnection conn, int param)
            {
                NetworkWriter writer = new NetworkWriter ();
                writer.WritePackedUInt32 ((uint)param);
                base.SendTargetRPCInternal (conn, typeof(class), "TargetTest", val);
            }
            public void CallTargetTest (NetworkConnection conn, int param)
            {
                // whatever the user did before
            }

            or if optional:
            public void TargetTest (int param)
            {
                NetworkWriter writer = new NetworkWriter ();
                writer.WritePackedUInt32 ((uint)param);
                base.SendTargetRPCInternal (null, typeof(class), "TargetTest", val);
            }
            public void CallTargetTest (int param)
            {
                // whatever the user did before
            }

            Originally HLAPI put the send message code inside the Call function
            and then proceeded to replace every call to TargetTest with CallTargetTest

            This method moves all the user's code into the "Call" method
            and replaces the body of the original method with the send message code.
            This way we do not need to modify the code anywhere else,  and this works
            correctly in dependent assemblies

        */
        public static MethodDefinition ProcessTargetRpcCall(TypeDefinition td, MethodDefinition md, CustomAttribute ca)
        {
            var rpc = new MethodDefinition("Call" + md.Name, MethodAttributes.Public |
                                                             MethodAttributes.HideBySig,
                Weaver.voidType);

            // add parameters
            foreach (var pd in md.Parameters)
                rpc.Parameters.Add(new ParameterDefinition(pd.Name, ParameterAttributes.None, pd.ParameterType));

            // move the old body to the new function
            var newBody = rpc.Body;
            rpc.Body = md.Body;
            md.Body = newBody;

            var rpcWorker = md.Body.GetILProcessor();

            NetworkBehaviourProcessor.WriteSetupLocals(rpcWorker);

            NetworkBehaviourProcessor.WriteCreateWriter(rpcWorker);

            // NetworkConnection parameter is optional
            var hasNetworkConnection = HasNetworkConnectionParameter(md);

            // write all the arguments that the user passed to the TargetRpc call
            // (skip first one if first one is NetworkConnection)
            if (!NetworkBehaviourProcessor.WriteArguments(rpcWorker, md, hasNetworkConnection))
                return null;

            var rpcName = md.Name;
            var index = rpcName.IndexOf(TargetRpcPrefix);
            if (index > -1) rpcName = rpcName.Substring(TargetRpcPrefix.Length);

            // invoke SendInternal and return
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldarg_0)); // this
            if (HasNetworkConnectionParameter(md))
                rpcWorker.Append(rpcWorker.Create(OpCodes.Ldarg_1)); // connection
            else
                rpcWorker.Append(rpcWorker.Create(OpCodes.Ldnull)); // null
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldtoken, td));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Call, Weaver.getTypeFromHandleReference)); // invokerClass
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldstr, rpcName));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldloc_0)); // writer
            rpcWorker.Append(rpcWorker.Create(OpCodes.Ldc_I4, NetworkBehaviourProcessor.GetChannelId(ca)));
            rpcWorker.Append(rpcWorker.Create(OpCodes.Callvirt, Weaver.sendTargetRpcInternal));

            NetworkBehaviourProcessor.WriteRecycleWriter(rpcWorker);

            rpcWorker.Append(rpcWorker.Create(OpCodes.Ret));

            return rpc;
        }

        public static bool ProcessMethodsValidateTargetRpc(MethodDefinition md, CustomAttribute ca)
        {
            if (!md.Name.StartsWith("Target"))
            {
                Weaver.Error($"{md} must start with Target.  Consider renaming it to Target{md.Name}");
                return false;
            }

            if (md.IsStatic)
            {
                Weaver.Error($"{md} must not be static");
                return false;
            }

            if (!NetworkBehaviourProcessor.ProcessMethodsValidateFunction(md)) return false;

            // validate
            return NetworkBehaviourProcessor.ProcessMethodsValidateParameters(md, ca);
        }
    }
}