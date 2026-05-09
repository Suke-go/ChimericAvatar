using System;
using NUnit.Framework;
using Chimera.Runtime;

namespace Chimera.Runtime.Tests
{
    public class SessionJoinTokenTests
    {
        [Test]
        public void Parse_AcceptsCanonicalUri()
        {
            var t = SessionJoinToken.Parse("chimera://session?code=ABC123&join=eyJhbGc.PAYLOAD.SIG");
            Assert.AreEqual("ABC123", t.SessionCode);
            Assert.AreEqual("eyJhbGc.PAYLOAD.SIG", t.JoinToken);
            Assert.IsNull(t.ApiBaseUrlOverride);
            Assert.IsTrue(t.IsValid);
        }

        [Test]
        public void Parse_AcceptsApiOverride_AndIsCaseInsensitiveOnScheme()
        {
            var t = SessionJoinToken.Parse("CHIMERA://session?code=X&join=Y&api=https%3A%2F%2Fstaging.example%2F");
            Assert.AreEqual("X", t.SessionCode);
            Assert.AreEqual("Y", t.JoinToken);
            Assert.AreEqual("https://staging.example/", t.ApiBaseUrlOverride);
        }

        [Test]
        public void Parse_RejectsWrongScheme()
        {
            Assert.Throws<FormatException>(() => SessionJoinToken.Parse("https://session?code=X&join=Y"));
        }

        [Test]
        public void Parse_RejectsWrongHost()
        {
            Assert.Throws<FormatException>(() => SessionJoinToken.Parse("chimera://other?code=X&join=Y"));
        }

        [Test]
        public void Parse_RejectsMissingQuery()
        {
            Assert.Throws<FormatException>(() => SessionJoinToken.Parse("chimera://session"));
        }

        [Test]
        public void Parse_RejectsMissingRequiredParams()
        {
            Assert.Throws<FormatException>(() => SessionJoinToken.Parse("chimera://session?code=X"));
            Assert.Throws<FormatException>(() => SessionJoinToken.Parse("chimera://session?join=Y"));
        }

        [Test]
        public void TryParse_ReportsErrorWithoutThrowing()
        {
            Assert.IsFalse(SessionJoinToken.TryParse("not-a-uri", out var t, out var err));
            Assert.IsNull(t);
            StringAssert.Contains("chimera://", err);
        }

        [Test]
        public void ToString_RoundTrips()
        {
            var input = SessionJoinToken.Parse("chimera://session?code=ABC123&join=eyJ.payload.sig");
            var roundTripped = SessionJoinToken.Parse(input.ToString());
            Assert.AreEqual(input.SessionCode, roundTripped.SessionCode);
            Assert.AreEqual(input.JoinToken, roundTripped.JoinToken);
        }
    }
}
