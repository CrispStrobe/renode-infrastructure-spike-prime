//
// Copyright (c) 2026 Brickwright contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.IO;
using NUnit.Framework;

namespace Antmicro.Renode.Utilities
{
    [TestFixture]
    public class ShippedTranslationLibrariesTests
    {
        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "renode-shipped-tlib-" + Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            System.IO.Directory.Delete(directory, true);
        }

        [Test]
        public void ShouldMapResourceNameToFileName()
        {
            Assert.AreEqual("translate-arm-m-le.so", ShippedTranslationLibraries.FileNameFor("Antmicro.Renode.translate-arm-m-le.so"));
        }

        [Test]
        public void ShouldNotClaimWhenNoFileIsShipped()
        {
            Assert.IsFalse(ShippedTranslationLibraries.TryClaim(directory, "Antmicro.Renode.translate-arm-m-le.so", out var path));
            Assert.IsNull(path);
        }

        [Test]
        public void ShouldClaimShippedFileOnceUntilReleased()
        {
            var file = Path.Combine(directory, "translate-arm-m-le.so");
            File.WriteAllText(file, "x");
            Assert.IsTrue(ShippedTranslationLibraries.TryClaim(directory, "Antmicro.Renode.translate-arm-m-le.so", out var first));
            Assert.AreEqual(Path.GetFullPath(file), first);
            Assert.IsTrue(ShippedTranslationLibraries.IsShipped(first));
            // A second CPU of the same architecture must not share the loaded image.
            Assert.IsFalse(ShippedTranslationLibraries.TryClaim(directory, "Antmicro.Renode.translate-arm-m-le.so", out var second));
            Assert.IsNull(second);
            Assert.IsTrue(ShippedTranslationLibraries.Release(first));
            Assert.IsFalse(ShippedTranslationLibraries.IsShipped(first));
            Assert.IsTrue(File.Exists(file), "a shipped library is never deleted");
            Assert.IsTrue(ShippedTranslationLibraries.TryClaim(directory, "Antmicro.Renode.translate-arm-m-le.so", out var again));
            ShippedTranslationLibraries.Release(again);
        }

        [Test]
        public void ShouldNotTreatExtractedFileAsShipped()
        {
            Assert.IsFalse(ShippedTranslationLibraries.IsShipped(Path.Combine(directory, "0-Antmicro.Renode.translate-arm-m-le.so")));
            Assert.IsFalse(ShippedTranslationLibraries.IsShipped(null));
        }

        private string directory;
    }
}
