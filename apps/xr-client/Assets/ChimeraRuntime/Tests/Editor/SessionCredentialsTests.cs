using System;
using NUnit.Framework;
using Chimera.Runtime;

namespace Chimera.Runtime.Tests
{
    public class SessionCredentialsTests
    {
        [Test]
        public void IsExpired_TrueWhenExpiryWithinSkew()
        {
            var c = new SessionCredentials
            {
                ExpiresAt = DateTime.UtcNow.AddSeconds(30).ToString("O"),
            };
            Assert.IsTrue(c.IsExpired(TimeSpan.FromSeconds(60)));
        }

        [Test]
        public void IsExpired_FalseWhenComfortablyInTheFuture()
        {
            var c = new SessionCredentials
            {
                ExpiresAt = DateTime.UtcNow.AddMinutes(15).ToString("O"),
            };
            Assert.IsFalse(c.IsExpired(TimeSpan.FromSeconds(60)));
        }

        [Test]
        public void IsExpired_FalseWhenExpiresAtUnparseable()
        {
            // Unknown expiry → don't preemptively refresh; let the wire decide.
            var c = new SessionCredentials { ExpiresAt = "not-a-date" };
            Assert.IsFalse(c.IsExpired());
        }

        [Test]
        public void ExpiresAtUtc_ConvertsLocalToUtc()
        {
            DateTime local = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Local);
            var c = new SessionCredentials { ExpiresAt = local.ToString("O") };
            Assert.AreEqual(local.ToUniversalTime(), c.ExpiresAtUtc);
        }
    }
}
