using NUnit.Framework;
using UnityEngine;

namespace Portage.Tests
{
    public sealed class PortageMappingTableTests
    {
        [Test]
        public void Defaults_ResolveOrmCaseInsensitive()
        {
            var table = PortageMappingTable.CreateDefault();
            try
            {
                PortageMappingTable.TextureRule rule = table.FindTextureRule("orm");

                Assert.IsNotNull(rule);
                Assert.AreEqual(TextureUsage.Mask, rule.usage);
                Assert.AreEqual(MaskChannel.Occlusion, rule.red);
                Assert.AreEqual(MaskChannel.Roughness, rule.green);
                Assert.AreEqual(MaskChannel.Metallic, rule.blue);
            }
            finally
            {
                Object.DestroyImmediate(table);
            }
        }
    }
}
