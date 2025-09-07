// Copyright (c) Martin Costello, 2024. All rights reserved.
// Licensed under the Apache 2.0 license. See the LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using MartinCostello.Benchmarks.PageModels;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace MartinCostello.Benchmarks;

[Collection(DashboardCollection.Name)]
public class DashboardTests(
    DashboardFixture fixture,
    ITestOutputHelper outputHelper) : UITest(outputHelper)
{
    private const string ValidFakeToken = "VALID_GITHUB_ACCESS_TOKEN";

    public static TheoryData<string, string?> Browsers()
    {
        var browsers = new TheoryData<string, string?>()
        {
            { BrowserType.Chromium, null },
            { BrowserType.Chromium, "chrome" },
            { BrowserType.Firefox, null },
        };

        return browsers;
    }

#pragma warning disable xUnit1013
    [ModuleInitializer]
    public static void InitPlaywright()
    {
        VerifyImageMagick.Initialize();
        VerifyImageMagick.RegisterComparers(threshold: 0.25);
        VerifyPlaywright.Initialize();
    }
#pragma warning restore xUnit1013

    [Theory]
    [MemberData(nameof(Browsers))]
    public async Task Can_View_Benchmarks(string browserType, string? browserChannel)
    {
        // Arrange
        var appSettingsFile = Path.Combine(DashboardFixture.GetApplicationDirectory(), "wwwroot", "appsettings.json");

        var appSettingsJson = await File.ReadAllTextAsync(appSettingsFile, cancellationToken: TestContext.Current.CancellationToken);
        using var settings = JsonDocument.Parse(appSettingsJson);
        var root = settings.RootElement.GetProperty("Dashboard");

        string[] expectedRepos =
            root.TryGetProperty("Repositories", out var reposEl) && reposEl.ValueKind == JsonValueKind.Array
                ? [.. reposEl.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x))]
                : throw new InvalidOperationException("No repositories array found in appsettings.json.");

        var options = new BrowserFixtureOptions
        {
            BrowserType = browserType,
            BrowserChannel = browserChannel,
        };

        var browser = new BrowserFixture(options, Output);
        await browser.WithPageAsync(async page =>
        {
            await page.GotoAsync(fixture.ServerAddress);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var authorized = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await ConfigureMocksAsync(page, cancelled, authorized);

            var dashboard = new HomePage(page);

            // Act and Assert
            await dashboard.WaitForContentAsync();
            await dashboard.Repository().ShouldBe(expectedRepos[0]);
            await dashboard.Branch().ShouldBe("main");

            await dashboard.Repositories().ShouldBe(expectedRepos);

            // await dashboard.Branches().ShouldBe(["main", "dotnet-nightly", "dotnet-vnext"]);
            var benchmarks = await dashboard.Benchmarks();

            benchmarks.Count.ShouldBeGreaterThan(0);

            // var chart = await dashboard.GetChart(
            //     expectedRepos[0],
            //     benchmarks[expectedRepos[0]][0]);

            // await VerifyScreenshot(chart, $"{browserType}_{browserChannel}_benchmarks-demo");

            // Arrange
            var token = await dashboard.SignInAsync();
            await token.WaitForContentAsync();

            // Act
            var firstCode = await token.UserCode();

            // Assert
            firstCode.ShouldNotBeNullOrWhiteSpace();

            // Act
            await token.Authorize();
            cancelled.SetResult(firstCode);

            // Assert
            await token.AuthorizationFailed().ShouldBeTrue();

            // Arrange
            await token.RefreshUserCode();
            await token.WaitForContentAsync();

            // Act
            var secondCode = await token.UserCode();

            // Assert
            secondCode.ShouldNotBeNullOrWhiteSpace();
            secondCode.ShouldNotBe(firstCode);

            // Act
            await token.Authorize();
            authorized.SetResult(secondCode);

            // Assert
            await dashboard.WaitForSignedInAsync();
            await dashboard.UserNameAsync().ShouldBe("speedy");
            await dashboard.Repository().ShouldBe(expectedRepos[0]);
            await dashboard.Branch().ShouldBe("main");

            await dashboard.Repositories().ShouldBe(expectedRepos);

            // Act
            await dashboard.SignOutAsync();

            // Assert
            await dashboard.WaitForSignedOutAsync();
        });
    }

    private static string JsonResponseFile(string name)
        => Path.Combine(".", "Responses", $"{name}.json");

    private static async Task ConfigureMocksAsync(
        IPage page,
        TaskCompletionSource<string> cancelled,
        TaskCompletionSource<string> authorized)
    {
        const string GitHubApi = "https://api.github.com";
        const string GitHubData = "https://raw.githubusercontent.com";
        const string GitHubLogin = "https://github.com/login/device";
        const string GitHubToken = "https://api.martincostello.com/github";
        const string Owner = "martincostello";

        page.Popup += async (_, popup) =>
        {
            await popup.WaitForLoadStateAsync();

            var uri = new Uri(popup.Url);

            uri.Host.ShouldBe("github.com");
            uri.Scheme.ShouldBe(Uri.UriSchemeHttps);
            uri.PathAndQuery.ShouldStartWith("/login");

            var query = QueryHelpers.ParseQuery(uri.Query);
            query.ShouldContainKeyAndValue("return_to", GitHubLogin);

            await popup.CloseAsync();
        };

        await ConfigureUserAsync(page, cancelled, authorized);
        await ConfigureRepoAsync(page, "benchmarks-demo", ["main"]);
        await ConfigureRepoAsync(page, "website", ["main", "dev"]);

        static async Task ConfigureRepoAsync(IPage page, string repo, string[] branches)
        {
            await page.RouteAsync($"{GitHubApi}/repos/{Owner}/{repo}", async (route) =>
            {
                await route.FulfillAsync(new()
                {
                    Path = JsonResponseFile($"{repo}-repo"),
                });
            });

            await page.RouteAsync($"{GitHubApi}/repos/{Owner}/{repo}/branches", async (route) =>
            {
                await route.FulfillAsync(new()
                {
                    Path = JsonResponseFile($"{repo}-branches"),
                });
            });

            foreach (var branch in branches)
            {
                await page.RouteAsync($"{GitHubData}/{Owner}/benchmarks/{branch}/{repo}/data.json", async (route) =>
                {
                    await route.FulfillAsync(new()
                    {
                        Path = JsonResponseFile($"{repo}-{branch}"),
                    });
                });
            }
        }

        static async Task ConfigureUserAsync(
            IPage page,
            TaskCompletionSource<string> cancelled,
            TaskCompletionSource<string> authorized)
        {
            string clientId = "Ov23likdXQFqdqFST1Ec";
            string scopes = "public_repo";

            string currentDeviceCode = GenerateDeviceCode();
            string currentUserCode = GenerateUserCode();

            object NewDeviceCode()
            {
                string newDeviceCode = GenerateDeviceCode();
                string newUserCode = GenerateUserCode();

                currentDeviceCode = newDeviceCode;
                currentUserCode = newUserCode;

                return new
                {
                    device_code = newDeviceCode,
                    user_code = newUserCode,
                    verification_uri = GitHubLogin,
                    expires_in = 899,
                    interval = 1,
                };
            }

            await page.RouteAsync($"{GitHubToken}/login/device/code?client_id={clientId}&scope={scopes}", async (route) =>
            {
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    Json = NewDeviceCode(),
                });
            });

            await page.RouteAsync($"{GitHubToken}/login/oauth/access_token?client_id={clientId}&*", async (route) =>
            {
                var url = new Uri(route.Request.Url);
                var query = QueryHelpers.ParseQuery(url.Query);

                string response;

                if (query["device_code"] == currentDeviceCode)
                {
                    if (authorized.Task.IsCompleted && await authorized.Task == currentUserCode)
                    {
                        response = "authorized";
                    }
                    else if (cancelled.Task.IsCompleted && await cancelled.Task == currentUserCode)
                    {
                        response = "expired";
                    }
                    else
                    {
                        response = "pending";
                    }
                }
                else
                {
                    response = "incorrect_device_code";
                }

                await route.FulfillAsync(new()
                {
                    Status = 200,
                    Path = JsonResponseFile($"access-token-{response}"),
                });
            });

            await page.RouteAsync($"{GitHubApi}/user", async (route) =>
            {
                const string Authorization = "authorization";

                route.Request.Headers.ShouldContainKey(Authorization);
                var token = route.Request.Headers[Authorization];

                if (token == $"token {ValidFakeToken}")
                {
                    await route.FulfillAsync(new()
                    {
                        Path = JsonResponseFile("user-valid-token"),
                        Status = 200,
                    });
                }
                else
                {
                    await route.FulfillAsync(new()
                    {
                        Path = JsonResponseFile("user-invalid-token"),
                        Status = 401,
                    });
                }
            });

            static string GenerateDeviceCode()
                => Guid.NewGuid().ToString().Replace("-", string.Empty, StringComparison.Ordinal);

            static string GenerateUserCode()
                => Guid.NewGuid().ToString().Substring(9, 9);
        }
    }
}
