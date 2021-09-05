using Neo.SmartContract.Framework;

namespace Neo.SmartContract
{
    public class ContainerState : Nep11TokenState
    {
        public UInt160 Maker;
        public string Category;
    }
}
