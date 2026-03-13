using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.Extensions;
using OpenQA.Selenium.Remote;

internal static class LingosBot
{
    private sealed class Credentials
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }

    private static readonly string BaseDirectoryPath = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string CredentialFile = Path.Combine(BaseDirectoryPath, "credentials.txt");
    private static readonly string VocabJson = Path.Combine(BaseDirectoryPath, "vocab.json");

    private const string BaseUrl = "https://lingos.pl";
    private static readonly string UrlHome = $"{BaseUrl}/home/start";
    private static readonly string UrlLogin = $"{BaseUrl}/sign-in";
    private static readonly string UrlKokpit = $"{BaseUrl}/student-confirmed/group";
    private static readonly string UrlZestawy = $"{BaseUrl}/student-confirmed/wordsets";

    private static readonly Regex ReNormalize = new Regex(@"[^0-9a-ząćęłńóśźżäöüß]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReGarbage = new Regex(@"^[^\wąćęłńóśźżÄÖÜäöüß]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReHasLetter = new Regex(@"[A-Za-zÄÖÜäöüßąćęłńóśźż]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IWebDriver driver = null!;
    private static int lessonsToDo = 1;

    private static void Main()
    {
        Credentials credentials = GetCredentials();
        lessonsToDo = GetLessonCount();

        driver = CreateDriver(headless: false);

        try
        {
            Login();
            Dictionary<string, string> vocab = LoadOrScrapeVocab(forceRescrape: true);
            Console.WriteLine($"Loaded {vocab.Count} vocab pairs.");
            RunLessons(vocab, lessonsToDo);
            Console.WriteLine("Finished.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fatal error:");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            try
            {
                driver.Quit();
            }
            catch
            {
            }
        }

        Credentials GetCredentials()
        {
            if (File.Exists(CredentialFile))
            {
                string[] lines = File.ReadAllLines(CredentialFile);
                if (lines.Length >= 2 &&
                    !string.IsNullOrWhiteSpace(lines[0]) &&
                    !string.IsNullOrWhiteSpace(lines[1]))
                {
                    Console.Write("Saved credentials found. Press Enter to use them or type R to replace: ");
                    string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                    if (answer != "r")
                    {
                        return new Credentials
                        {
                            Email = lines[0].Trim(),
                            Password = lines[1]
                        };
                    }
                }
            }

            Console.Write("Enter your Lingos email/login: ");
            string email = (Console.ReadLine() ?? "").Trim();

            Console.Write("Enter your Lingos password: ");
            string password = ReadPassword();

            File.WriteAllLines(CredentialFile, new[] { email, password });

            return new Credentials
            {
                Email = email,
                Password = password
            };
        }

        void Login()
        {
            if (string.IsNullOrWhiteSpace(credentials.Email) || string.IsNullOrWhiteSpace(credentials.Password))
            {
                throw new InvalidOperationException("Email or password is empty.");
            }

            driver.Navigate().GoToUrl(UrlHome);
            AcceptCookiesIfAny();

            try
            {
                ClickAnyXPath(new[]
                {
                    "//a[contains(@href,'/sign-in')]",
                    "//*[self::a or self::button][contains(normalize-space(.),'Logowanie')]",
                    "//*[self::a or self::button][contains(normalize-space(.),'Zaloguj')]",
                }, timeoutEachSeconds: 2.5);
            }
            catch
            {
                driver.Navigate().GoToUrl(UrlLogin);
            }

            IWebElement emailInput = new WebDriverWait(driver, TimeSpan.FromSeconds(8))
                .Until(d => d.FindElement(By.Name("login")));

            IWebElement passInput = new WebDriverWait(driver, TimeSpan.FromSeconds(8))
                .Until(d => d.FindElement(By.Name("password")));

            SafeType(emailInput, credentials.Email);
            SafeType(passInput, credentials.Password);

            ClickAnyXPath(new[]
            {
                "//button[contains(., 'Zaloguj się')]",
                "//button[@type='submit']",
            }, timeoutEachSeconds: 2.5);

            new WebDriverWait(driver, TimeSpan.FromSeconds(10))
                .Until(d => d.Url.Contains("/student-confirmed"));
        }
    }

    private static int GetLessonCount()
    {
        while (true)
        {
            Console.Write("How many lessons to do? (1-5): ");
            string raw = (Console.ReadLine() ?? "").Trim();

            if (int.TryParse(raw, out int value) && value >= 1 && value <= 5)
            {
                return value;
            }

            Console.WriteLine("Enter a number from 1 to 5.");
        }
    }

    private static string ReadPassword()
    {
        StringBuilder sb = new StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }
    }

    private static IWebDriver CreateDriver(bool headless = false)
    {
        ChromeOptions options = new ChromeOptions();
        options.AddArgument("--start-maximized");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--disable-extensions");
        options.AddArgument("--disable-popup-blocking");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddArgument("--mute-audio");
        options.PageLoadStrategy = PageLoadStrategy.Eager;

        options.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);
        // options.AddUserProfilePreference("profile.managed_default_content_settings.fonts", 2);

        if (headless)
        {
            options.AddArgument("--headless=new");
        }

        IWebDriver created = new ChromeDriver(options);
        created.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(20);
        return created;
    }

    private static T WaitForValue<T>(Func<T?> fn, double timeoutSeconds = 2.0, int pollMs = 30, string error = "timeout")
        where T : class
    {
        DateTime end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Exception? last = null;

        while (DateTime.UtcNow < end)
        {
            try
            {
                T? value = fn();
                if (value != null)
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            Thread.Sleep(pollMs);
        }

        throw new WebDriverTimeoutException($"{error}. last_err={last?.Message}");
    }

    private static void WaitForCondition(Func<bool> fn, double timeoutSeconds = 2.0, int pollMs = 30, string error = "timeout")
    {
        DateTime end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Exception? last = null;

        while (DateTime.UtcNow < end)
        {
            try
            {
                if (fn())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            Thread.Sleep(pollMs);
        }

        throw new WebDriverTimeoutException($"{error}. last_err={last?.Message}");
    }

    private static bool Visible(IWebElement element)
    {
        try
        {
            return element.Displayed;
        }
        catch
        {
            return false;
        }
    }

    private static IWebElement? FirstVisibleEnabled(IEnumerable<IWebElement> elements)
    {
        foreach (IWebElement el in elements)
        {
            try
            {
                if (el.Displayed && el.Enabled)
                {
                    return el;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string CleanText(string? s)
    {
        string text = s ?? "";
        text = text.Replace("\u00a0", " ");
        text = text.Replace("\ufeff", "");
        text = text.Replace("\u200b", "");
        text = text.Trim();
        text = text.Trim(' ', '\t', '\r', '\n', ',', '.', ';', ':', '–', '-');
        return text;
    }

    private static bool HasLetter(string? s)
    {
        return ReHasLetter.IsMatch((s ?? "").Trim());
    }

    private static string NormalizeKey(string s)
    {
        string text = CleanText(s).ToLowerInvariant();
        text = ReNormalize.Replace(text, " ");
        return string.Join(" ", text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsGarbageText(string s)
    {
        string text = CleanText(s);
        return string.IsNullOrWhiteSpace(text) || ReGarbage.IsMatch(text);
    }

    private static IWebElement SmartClick(By locator, double timeoutSeconds = 6)
    {
        IWebElement element = WaitForValue(() =>
        {
            IReadOnlyCollection<IWebElement> elements = driver.FindElements(locator);
            return elements.FirstOrDefault();
        }, timeoutSeconds: timeoutSeconds, pollMs: 20, error: $"smart_click cannot find {locator}");

        try
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({block:'center'});", element);
        }
        catch
        {
        }

        try
        {
            element.Click();
            return element;
        }
        catch
        {
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element);
            return element;
        }
    }

    private static IWebElement ClickAnyXPath(IEnumerable<string> xpaths, double timeoutEachSeconds = 2.0)
    {
        Exception? last = null;

        foreach (string xp in xpaths)
        {
            try
            {
                return SmartClick(By.XPath(xp), timeoutSeconds: timeoutEachSeconds);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new WebDriverTimeoutException($"click_any_xpath failed. last_err={last?.Message}");
    }

    private static void AcceptCookiesIfAny()
    {
        string[] cookieXPaths =
        {
            "//button[contains(., 'Zezwól na wszystkie')]",
            "//button[contains(., 'Akceptuj')]",
            "//button[contains(., 'Accept')]",
            "//*[self::button or self::a][contains(., 'Zezwól')]",
        };

        try
        {
            ClickAnyXPath(cookieXPaths, timeoutEachSeconds: 1.2);
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> ExtractVocabFromCurrentSet()
    {
        IReadOnlyCollection<IWebElement> leftElems = driver.FindElements(By.CssSelector(".flashcard-border-end"));
        IReadOnlyCollection<IWebElement> rightElems = driver.FindElements(By.CssSelector(".flashcard-border-start"));

        Dictionary<string, string> vocab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((IWebElement left, IWebElement right) in leftElems.Zip(rightElems, (l, r) => (l, r)))
        {
            string german = CleanText(left.Text);
            string polish = CleanText(right.Text);

            if (string.IsNullOrWhiteSpace(polish) || string.IsNullOrWhiteSpace(german))
            {
                continue;
            }

            if (IsGarbageText(polish) || IsGarbageText(german))
            {
                continue;
            }

            if (!HasLetter(polish) || !HasLetter(german))
            {
                continue;
            }

            vocab[polish] = german;
        }

        return vocab;
    }

    private static Dictionary<string, string> ScrapeAllZestawy()
    {
        driver.Navigate().GoToUrl(UrlZestawy);
        new WebDriverWait(driver, TimeSpan.FromSeconds(10)).Until(d => d.Url.Contains("/student-confirmed/wordsets"));

        const string PodgladHrefXPath = "//a[contains(@href, '/student-confirmed/wordset/')]";

        new WebDriverWait(driver, TimeSpan.FromSeconds(10))
            .Until(d => d.FindElements(By.XPath(PodgladHrefXPath)).Count > 0);

        List<string> hrefs = new List<string>();

        foreach (IWebElement el in driver.FindElements(By.XPath(PodgladHrefXPath)))
        {
            try
            {
                string href = el.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href) && !hrefs.Contains(href, StringComparer.OrdinalIgnoreCase))
                {
                    hrefs.Add(href);
                }
            }
            catch (StaleElementReferenceException)
            {
            }
        }

        Dictionary<string, string> allVocab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string href in hrefs)
        {
            driver.Navigate().GoToUrl(href);

            new WebDriverWait(driver, TimeSpan.FromSeconds(10))
                .Until(d => d.FindElements(By.CssSelector(".flashcard-border-start")).Count > 0);

            foreach (KeyValuePair<string, string> kvp in ExtractVocabFromCurrentSet())
            {
                allVocab[kvp.Key] = kvp.Value;
            }
        }

        return allVocab;
    }

    private static Dictionary<string, string> CleanVocabDictionary(Dictionary<string, string> vocab)
    {
        Dictionary<string, string> cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> kvp in vocab)
        {
            string kk = CleanText(kvp.Key);
            string vv = CleanText(kvp.Value);

            if (string.IsNullOrWhiteSpace(kk) || string.IsNullOrWhiteSpace(vv))
            {
                continue;
            }

            if (IsGarbageText(kk) || IsGarbageText(vv))
            {
                continue;
            }

            if (!HasLetter(kk) || !HasLetter(vv))
            {
                continue;
            }

            cleaned[kk] = vv;
        }

        return cleaned;
    }

    private static Dictionary<string, string> LoadOrScrapeVocab(bool forceRescrape = false)
    {
        if (!forceRescrape && File.Exists(VocabJson))
        {
            Dictionary<string, string>? vocabFromFile = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(VocabJson)
            );

            Dictionary<string, string> cleanedFromFile = CleanVocabDictionary(
                vocabFromFile ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            );

            File.WriteAllText(VocabJson, JsonSerializer.Serialize(cleanedFromFile, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            return cleanedFromFile;
        }

        Dictionary<string, string> scraped = ScrapeAllZestawy();
        Dictionary<string, string> cleaned = CleanVocabDictionary(scraped);

        File.WriteAllText(VocabJson, JsonSerializer.Serialize(cleaned, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        return cleaned;
    }

    private static bool CloseLessonFinishedModalIfAny()
    {
        IReadOnlyCollection<IWebElement> buttons = driver.FindElements(By.XPath("//button[contains(., 'Zamknij')]"));

        foreach (IWebElement button in buttons)
        {
            if (!Visible(button))
            {
                continue;
            }

            try
            {
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", button);
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static void ClickUczSie()
    {
        CloseLessonFinishedModalIfAny();
        SmartClick(By.XPath("//a[contains(@href, '/lesson/')]"), timeoutSeconds: 8);
    }

    private static void FastSetInputValue(IWebElement inputEl, string value)
    {
        string finalValue = value ?? "";

        ((IJavaScriptExecutor)driver).ExecuteScript(@"
            const el = arguments[0];
            const val = arguments[1];
            el.focus();
            const setter =
                Object.getOwnPropertyDescriptor(el.__proto__, 'value')?.set
                || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
            if (setter) setter.call(el, val);
            else el.value = val;
            el.dispatchEvent(new Event('input',  { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
        ", inputEl, finalValue);
    }

    private static void SafeType(IWebElement inputEl, string value)
    {
        try
        {
            inputEl.Clear();
            inputEl.SendKeys(value);
        }
        catch
        {
            FastSetInputValue(inputEl, value);
        }
    }

    private static void AdvanceCard()
    {
        try
        {
            IWebElement btn = driver.FindElement(By.Id("enterBtn"));
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", btn);
            return;
        }
        catch
        {
        }

        try
        {
            new Actions(driver).SendKeys(Keys.Enter).Perform();
        }
        catch
        {
        }
    }

    private static bool DoOneLesson(Dictionary<string, string> vocabNorm)
    {
        while (true)
        {
            if (driver.Url.Contains("/group/finished", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (CloseLessonFinishedModalIfAny())
            {
                return true;
            }

            IWebElement mainEl = WaitForValue(() =>
            {
                try
                {
                    return driver.FindElement(By.Id("flashcard_main_text"));
                }
                catch
                {
                    return null;
                }
            }, timeoutSeconds: 3.0, pollMs: 20, error: "no main text");

            string prompt = CleanText(mainEl.Text);
            string promptKey = NormalizeKey(prompt);

            IWebElement? inputEl = FirstVisibleEnabled(driver.FindElements(By.Id("flashcard_answer_input")));
            bool canType = inputEl != null;

            if (canType)
            {
                string answer = vocabNorm.TryGetValue(promptKey, out string? found) ? CleanText(found) : "";

                if (string.IsNullOrWhiteSpace(answer) || !HasLetter(answer) || IsGarbageText(answer))
                {
                    answer = "";
                }

                FastSetInputValue(inputEl!, answer);
            }

            AdvanceCard();

            bool Changed()
            {
                if (driver.Url.Contains("/group/finished", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (CloseLessonFinishedModalIfAny())
                {
                    return true;
                }

                try
                {
                    string nowText = CleanText(driver.FindElement(By.Id("flashcard_main_text")).Text);
                    if (NormalizeKey(nowText) != promptKey)
                    {
                        return true;
                    }
                }
                catch
                {
                }

                IWebElement? nowInput = FirstVisibleEnabled(driver.FindElements(By.Id("flashcard_answer_input")));
                bool nowCanType = nowInput != null;
                return nowCanType != canType;
            }

            try
            {
                WaitForCondition(Changed, timeoutSeconds: 1.2, pollMs: 40, error: "state did not change");
            }
            catch (WebDriverTimeoutException)
            {
                if (canType)
                {
                    inputEl = FirstVisibleEnabled(driver.FindElements(By.Id("flashcard_answer_input")));
                    if (inputEl != null)
                    {
                        string retryAns = vocabNorm.TryGetValue(promptKey, out string? found) ? CleanText(found) : "";
                        if (string.IsNullOrWhiteSpace(retryAns) || !HasLetter(retryAns) || IsGarbageText(retryAns))
                        {
                            retryAns = "";
                        }

                        FastSetInputValue(inputEl, retryAns);
                    }
                }

                AdvanceCard();
                WaitForCondition(Changed, timeoutSeconds: 1.2, pollMs: 40, error: "state did not change (retry)");
            }
        }
    }

    private static void RunLessons(Dictionary<string, string> vocab, int lessons)
    {
        Dictionary<string, string> vocabNorm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> kvp in vocab)
        {
            vocabNorm[NormalizeKey(kvp.Key)] = CleanText(kvp.Value);
        }

        driver.Navigate().GoToUrl(UrlKokpit);
        new WebDriverWait(driver, TimeSpan.FromSeconds(10)).Until(d => d.Url.Contains("/student-confirmed/group"));

        int done = 0;

        while (done < lessons)
        {
            CloseLessonFinishedModalIfAny();

            if (driver.Url.Contains("/group/finished", StringComparison.OrdinalIgnoreCase))
            {
                driver.Navigate().GoToUrl(UrlKokpit);
            }

            ClickUczSie();
            DoOneLesson(vocabNorm);
            done++;

            CloseLessonFinishedModalIfAny();

            if (!driver.Url.Contains("/student-confirmed/group", StringComparison.OrdinalIgnoreCase))
            {
                driver.Navigate().GoToUrl(UrlKokpit);
            }
        }
    }
}
