using System;
using OpcDaToUaGateway.Services;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    public class LicenseTrialClockTests
    {
        [Fact]
        public void GetElapsedSeconds_AddsPersistedAndCurrentRuntime()
        {
            Assert.Equal(150, LicenseManager.GetElapsedSeconds(120, TimeSpan.FromSeconds(30.9)));
        }

        [Fact]
        public void GetRemaining_ClampsExpiredTrialToZero()
        {
            TimeSpan remaining = LicenseManager.GetRemaining(100, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(120));

            Assert.Equal(TimeSpan.Zero, remaining);
        }

        [Fact]
        public void GetElapsedSeconds_ClampsNegativeValuesToZero()
        {
            Assert.Equal(0, LicenseManager.GetElapsedSeconds(-10, TimeSpan.FromSeconds(-5)));
        }
    }
}
