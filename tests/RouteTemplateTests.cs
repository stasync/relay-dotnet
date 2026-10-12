namespace UdpRelay.Tests
{
    public class RouteTemplateTests
    {
        [Fact]
        public void MatchCapturesParameters()
        {
            var template = new RouteTemplate("/events/{event}/subscriber/{name}");

            Assert.True(template.TryMatch(RouteTemplate.SplitPath("/events/build/subscriber/Alice"), out var routeValues));
            Assert.Equal("build", routeValues["event"]);
            Assert.Equal("Alice", routeValues["name"]);
        }

        [Theory]
        [InlineData("/events/build/subscribers")]
        [InlineData("/EVENTS/build/Subscribers")]
        [InlineData("/events/build/subscribers/")]
        [InlineData("//events//build/subscribers")]
        public void MatchIgnoresLiteralCaseAndEmptySegments(string path)
        {
            var template = new RouteTemplate("/events/{event}/subscribers");

            Assert.True(template.TryMatch(RouteTemplate.SplitPath(path), out var routeValues));
            Assert.Equal("build", routeValues["event"]);
        }

        [Theory]
        [InlineData("/events/build")]
        [InlineData("/events/build/subscribers/extra")]
        [InlineData("/events/build/subscriber")]
        [InlineData("/")]
        public void MatchFailsOnOtherPaths(string path)
        {
            var template = new RouteTemplate("/events/{event}/subscribers");

            Assert.False(template.TryMatch(RouteTemplate.SplitPath(path), out _));
        }

        [Fact]
        public void SplitPathUnescapesEachSegment()
        {
            Assert.Equal(["events", "a/b", "carol smith"], RouteTemplate.SplitPath("/events/a%2Fb/carol%20smith"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("events/{event}")]
        [InlineData("/events/{}")]
        [InlineData("/events/sub-{name}")]
        [InlineData("/events/{event}/subscriber/{event}")]
        public void InvalidTemplateThrows(string template)
        {
            Assert.Throws<ArgumentException>(() => new RouteTemplate(template));
        }

        [Fact]
        public void SameShapeIgnoresParameterNamesAndLiteralCase()
        {
            var template = new RouteTemplate("/events/{event}/subscribers");

            Assert.True(template.HasSameShapeAs(new RouteTemplate("/Events/{other}/subscribers")));
            Assert.False(template.HasSameShapeAs(new RouteTemplate("/events/{event}/{name}")));
            Assert.False(template.HasSameShapeAs(new RouteTemplate("/events/{event}")));
        }
    }
}