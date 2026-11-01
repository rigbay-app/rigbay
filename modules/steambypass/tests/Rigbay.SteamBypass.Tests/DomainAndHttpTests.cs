using System.Net;
using System.Text;
using Rigbay.SteamBypass.Net;

namespace Rigbay.SteamBypass.Tests;

public class DomainSetTests
{
    private static DomainSet Set(params string[] lines) => new(lines);

    [Theory]
    [InlineData("steampowered.com", true)]
    [InlineData("store.steampowered.com", true)]
    [InlineData("a.b.c.steampowered.com", true)]
    [InlineData("STORE.SteamPowered.COM", true)]
    [InlineData("store.steampowered.com.", true)]          // absolute name with the trailing dot
    [InlineData("evilsteampowered.com", false)]            // a suffix of the text is not a suffix of the labels
    [InlineData("steampowered.com.evil.example", false)]
    [InlineData("steampowered.co", false)]
    [InlineData("com", false)]
    [InlineData("", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("[::1]", false)]
    [InlineData("cache8-hkg1.steamcontent.com", true)]
    [InlineData("steamcdn-a.akamaihd.net", true)]
    [InlineData("other.akamaihd.net", false)]              // only the listed Akamai names, not the whole CDN
    public void Matching_is_by_whole_labels_and_ignores_case_and_a_trailing_dot(string host, bool expected)
    {
        Assert.Equal(expected, DomainSet.Default().Contains(host));
    }

    [Fact]
    public void Null_and_absurd_hosts_never_match_and_never_throw()
    {
        var set = DomainSet.Default();
        Assert.False(set.Contains(null));
        Assert.False(set.Contains(new string('a', 5000) + ".steampowered.com"));
        Assert.False(set.Contains("."));
        Assert.False(set.Contains("..."));
    }

    [Fact]
    public void The_file_format_is_the_old_one_comments_blanks_case_and_duplicates()
    {
        var set = Set("# a comment", "", "  Example.COM  ", "example.com", "  # indented comment", "sub.example.org");
        Assert.Equal(new[] { "example.com", "sub.example.org" }, set.Domains);
        Assert.Equal(0, set.Dropped);
    }

    [Fact]
    public void A_list_with_no_usable_line_falls_back_to_the_built_in_list()
    {
        var set = Set("# nothing here", "", "   ");
        Assert.Equal("built-in", set.Source);
        Assert.Equal(DomainSet.BuiltIn, set.Domains);
    }

    [Fact]
    public void Lines_that_are_not_plain_domain_names_are_dropped_and_never_reach_the_pac()
    {
        // A quote or a bracket in a list entry would break out of the string in the PAC script.
        var set = Set("good.example", "bad'; return 'DIRECT", "x\"y.example", "a b.example", "evil.example/path", "[::1]", "-lead.example", "a..b.example");
        Assert.Equal(new[] { "good.example" }, set.Domains);
        Assert.Equal(7, set.Dropped);
        var pac = set.BuildPac(18081);
        Assert.DoesNotContain("bad", pac);
        Assert.DoesNotContain("evil", pac);
        Assert.Contains("'good.example'", pac);
    }

    [Fact]
    public void A_leading_wildcard_or_dot_is_accepted_as_the_domain_itself()
    {
        Assert.Equal(new[] { "example.com", "example.net" }, Set("*.example.com", ".example.net").Domains);
    }

    [Fact]
    public void The_shipped_list_file_matches_the_built_in_list()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "module", "steam-domains.txt");
        var set = DomainSet.Load(new[] { path });
        Assert.Equal(path, set.Source);
        Assert.Equal(DomainSet.BuiltIn.OrderBy(s => s), set.Domains.OrderBy(s => s));
        Assert.Equal(0, set.Dropped);
    }

    [Fact]
    public void Load_takes_the_first_file_that_exists_and_has_a_domain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigbay-steambypass-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var empty = Path.Combine(dir, "empty.txt");
        var mine = Path.Combine(dir, "mine.txt");
        File.WriteAllText(empty, "# only a comment\n");
        File.WriteAllText(mine, "my.example\n");
        var set = DomainSet.Load(new[] { Path.Combine(dir, "missing.txt"), empty, mine });
        Assert.Equal(mine, set.Source);
        Assert.Equal(new[] { "my.example" }, set.Domains);
        Assert.Equal("built-in", DomainSet.Load(new[] { Path.Combine(dir, "missing.txt") }).Source);
    }

    [Fact]
    public void The_pac_sends_listed_hosts_to_the_proxy_and_everything_else_direct()
    {
        var pac = Set("steampowered.com", "steamcommunity.com").BuildPac(18081);
        Assert.StartsWith("function FindProxyForURL(url, host) {", pac);
        Assert.Contains("var d = ['steampowered.com','steamcommunity.com'];", pac);
        Assert.Contains("return 'PROXY 127.0.0.1:18081; DIRECT';", pac);
        Assert.EndsWith("return 'DIRECT';\n}\n", pac);
        // The Windows script engine is ES3: no endsWith, no arrow functions, no let/const.
        Assert.DoesNotContain("endsWith", pac);
        Assert.DoesNotContain("=>", pac);
        Assert.DoesNotContain("let ", pac);
        Assert.DoesNotContain("const ", pac);
    }

    [Fact]
    public void The_pac_uses_the_port_it_is_given()
    {
        Assert.Contains("PROXY 127.0.0.1:4242;", DomainSet.Default().BuildPac(4242));
    }
}

public class HttpHeadTests
{
    private static Task<HttpHead?> Read(string text, byte[]? initial = null) =>
        HttpHead.ReadAsync(new MemoryStream(Encoding.Latin1.GetBytes(text)), initial, CancellationToken.None);

    [Fact]
    public async Task A_head_is_split_from_the_bytes_after_it()
    {
        var head = await Read("GET /a HTTP/1.1\r\nHost: x\r\n\r\nBODY");
        Assert.NotNull(head);
        Assert.Equal("GET /a HTTP/1.1\r\nHost: x\r\n\r\n", head!.Text);
        Assert.Equal("BODY", Encoding.ASCII.GetString(head.Leftover));
        Assert.Equal(new RequestLine("GET", "/a", "HTTP/1.1"), head.Request);
        Assert.Equal("x", head.Header("HOST"));
        Assert.Null(head.Header("Content-Length"));
    }

    [Fact]
    public async Task The_end_of_the_head_is_found_when_it_arrives_in_pieces()
    {
        var s = new ChunkedStream(new[] { "GET / HTTP/1.1\r", "\nHost: a\r\n\r", "\nrest" });
        var head = await HttpHead.ReadAsync(s, null, CancellationToken.None);
        Assert.Equal("a", head!.Header("host"));
        Assert.Equal("rest", Encoding.ASCII.GetString(head.Leftover));
    }

    [Fact]
    public async Task Bytes_that_were_already_read_count_as_the_start_of_the_head()
    {
        var head = await Read("\r\nHost: a\r\n\r\n", Encoding.ASCII.GetBytes("GET / HTTP/1.1"));
        Assert.Equal("a", head!.Header("host"));
    }

    [Fact]
    public async Task A_stream_that_ends_first_or_a_head_that_is_too_long_gives_null()
    {
        Assert.Null(await Read("GET / HTTP/1.1\r\nHost: a\r\n"));
        Assert.Null(await Read(""));
        Assert.Null(await Read("GET / HTTP/1.1\r\n" + new string('x', HttpHead.MaxBytes + 10) + "\r\n\r\n"));
    }

    [Fact]
    public async Task Bytes_above_127_survive_the_round_trip()
    {
        var bytes = new byte[] { (byte)'G', (byte)'E', (byte)'T', (byte)' ', (byte)'/', (byte)' ', (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/', (byte)'1', (byte)'.', (byte)'1',
            13, 10, (byte)'X', (byte)':', (byte)' ', 0xC3, 0xA9, 13, 10, 13, 10 };
        var head = await HttpHead.ReadAsync(new MemoryStream(bytes), null, CancellationToken.None);
        Assert.Equal(bytes, Encoding.Latin1.GetBytes(head!.Text));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1", true)]
    [InlineData("CONNECT a.b:443 HTTP/1.1", true)]
    [InlineData("GET /", false)]
    [InlineData("GET / FTP/1.0", false)]
    [InlineData("get / HTTP/1.1", false)]
    [InlineData("GET  / HTTP/1.1", false)]
    [InlineData("", false)]
    [InlineData("GET / HTTP/1.1 extra", false)]
    public async Task Only_a_well_formed_request_line_parses(string line, bool ok)
    {
        var head = await Read(line + "\r\n\r\n");
        Assert.Equal(ok, head!.Request is not null);
    }

    [Theory]
    [InlineData("example.com:443", 443, "example.com", true)]
    [InlineData("example.com", 443, "example.com", true)]
    [InlineData("example.com:1", 1, "example.com", true)]
    [InlineData("example.com:65535", 65535, "example.com", true)]
    [InlineData("example.com:0", 0, "", false)]
    [InlineData("example.com:65536", 0, "", false)]
    [InlineData("example.com:-1", 0, "", false)]
    [InlineData("example.com:abc", 0, "", false)]
    [InlineData("example.com:99999999999999999999", 0, "", false)]
    [InlineData("example.com:", 0, "", false)]
    [InlineData(":443", 0, "", false)]
    [InlineData("", 0, "", false)]
    [InlineData("exa mple.com:443", 0, "", false)]
    [InlineData("exa\0mple.com:443", 0, "", false)]
    [InlineData("exämple.com:443", 0, "", false)]
    [InlineData("[::1]:443", 443, "::1", true)]
    [InlineData("[::1", 0, "", false)]
    public void Host_and_port_are_split_and_checked(string target, int port, string host, bool ok)
    {
        bool result = HttpHead.TrySplitHostPort(target, 443, out var h, out var p);
        Assert.Equal(ok, result);
        if (ok) { Assert.Equal(host, h); Assert.Equal(port, p); }
    }

    [Fact]
    public void An_absurdly_long_target_is_refused()
    {
        Assert.False(HttpHead.TrySplitHostPort(new string('a', 400) + ":443", 443, out _, out _));
    }

    private sealed class ChunkedStream(string[] chunks) : Stream
    {
        private int _i;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_i >= chunks.Length) return ValueTask.FromResult(0);
            var b = Encoding.Latin1.GetBytes(chunks[_i++]);
            b.CopyTo(buffer);
            return ValueTask.FromResult(b.Length);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class AddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.9.10", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("23.45.67.89", false)]
    [InlineData("104.18.12.34", false)]
    [InlineData("2606:4700::1111", false)]
    public void The_bypass_never_connects_to_itself_or_to_special_addresses(string address, bool forbidden)
    {
        Assert.Equal(forbidden, UpstreamDialer.IsForbidden(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task A_name_that_resolves_to_loopback_is_refused_before_any_connection()
    {
        var resolver = new FakeResolver();
        resolver.Map["loop.example"] = new[] { IPAddress.Loopback };
        var dialer = new UpstreamDialer(resolver, DomainSet.Default());
        await Assert.ThrowsAsync<ForbiddenTargetException>(() => dialer.ConnectAsync("loop.example", 80, true, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenTargetException>(() => dialer.ConnectAsync("127.0.0.1", 80, true, CancellationToken.None));
    }

    [Fact]
    public async Task Only_listed_names_are_resolved_over_doh_unless_the_caller_forces_it()
    {
        var resolver = new FakeResolver();
        resolver.Map["store.steampowered.com"] = new[] { IPAddress.Loopback };   // refused afterwards; only the resolve call is looked at
        resolver.Map["other.example"] = new[] { IPAddress.Loopback };
        var dialer = new UpstreamDialer(resolver, DomainSet.Default());
        await Assert.ThrowsAsync<ForbiddenTargetException>(() => dialer.ConnectAsync("store.steampowered.com", 443, false, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenTargetException>(() => dialer.ConnectAsync("other.example", 443, false, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenTargetException>(() => dialer.ConnectAsync("other.example", 80, true, CancellationToken.None));
        Assert.Equal(new[] { ("store.steampowered.com", true), ("other.example", false), ("other.example", true) }, resolver.Asked.ToArray());
    }
}
