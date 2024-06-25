using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.AspNetCore;
using Ganss.Xss;
using HtmlBuilders;
using LiteDB;
using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Htmx;
using static HtmlBuilders.HtmlTags;

const string DisplayDateFormat = "MMMM dd, yyyy";
const string HomePageName = "home-page";
const string HtmlMime = "text/html";

var builder = WebApplication.CreateBuilder();
builder.Services
    .AddSingleton<Wiki>()
    .AddAntiforgery()
    .AddMemoryCache();

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
app.UseAntiforgery();

// Load home page
app.MapGet("/", () => Results.Text(WikiPage(), HtmlMime));

app.MapGet("/new-page", (string? pageName, Wiki wiki, HttpContext context, IAntiforgery antiforgery) =>
{
    if (string.IsNullOrEmpty(pageName))
        Results.BadRequest("Page name is missing");

    var page = ToKebabCase(pageName!);

    var pageWiki = wiki.GetPage(page);
    if (pageWiki is not null)
        return Results.BadRequest("Page already exists");

    var list = Div.Id("allPages").Attribute("hx-swap-oob", "innerHTML");
    list = list.Append(Br)
        .Append(Span.Class("uk-label").Append("Pages"))
        .Append(AllPagesForEditing(wiki));

    return Results.Text(
        BuildForm(new PageInput(null, page, "", null), antiforgery.GetAndStoreTokens(context)) +
        list.ToHtmlString() +
        Title.Append(page).ToHtmlString()
        , HtmlMime);

    // Copied from https://www.30secondsofcode.org/c-sharp/s/to-kebab-case
    string ToKebabCase(string str)
    {
        var pattern = new Regex(@"[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+");
        return string.Join("-", pattern.Matches(str)).ToLower();
    }
});

// Edit a wiki page
app.MapGet("/edit", (string pageName, HttpContext context, Wiki wiki, IAntiforgery antiForgery, HttpRequest request) =>
{
    if (!request.IsHtmx())
        return Results.Text(WikiPage($"edit?pageName={pageName}"), HtmlMime);
    
    var page = wiki.GetPage(pageName);
    if (page is null)
        return Results.NotFound($"Page {pageName} not found");

    var list = Div.Id("allPages").Attribute("hx-swap-oob", "innerHTML");

    // Do not show delete button on home page
    if (!pageName.Equals(HomePageName, StringComparison.Ordinal))
        list = list.Append(RenderDeletePageButton(page, antiForgery.GetAndStoreTokens(context)));

    list = list.Append(Br)
        .Append(Span.Class("uk-label").Append("Pages"))
        .Append(AllPagesForEditing(wiki));


    return Results.Text(BuildForm(new PageInput(page.Id, pageName, page.Content, null),
                            antiForgery.GetAndStoreTokens(context)) +
                        RenderPageAttachmentsForEdit(page, antiForgery.GetAndStoreTokens(context)) +
                        list.ToHtmlString()
        , HtmlMime);
});

// Deal with attachment download
app.MapGet("/attachment", (string fileId, Wiki wiki) =>
{
    var file = wiki.GetFile(fileId);
    if (file is null)
        return Results.NotFound();

    app.Logger.LogInformation("Attachment {Id} - {Filename}", file.Value.meta.Id, file.Value.meta.Filename);

    return Results.File(file.Value.file, file.Value.meta.MimeType);
});

// Load a wiki page
app.MapGet("/{pageName}", (string pageName, Wiki wiki, HttpRequest request) =>
{
    if (!request.IsHtmx())
        return Results.Text(WikiPage(pageName), HtmlMime);


    var page = wiki.GetPage(pageName);

    if (page is not null)
        return Results.Text(
            H1.Append(KebabToNormalCase(page.Name)).ToHtmlString() +
            RenderPageContent(page) +
            RenderPageAttachments(page) +
            Div.Class("last-modified")
                .Append("Last modified: " + page.LastModifiedUtc.ToString(DisplayDateFormat))
                .ToHtmlString() +
            A.Attribute("hx-get", $"/edit?pageName={pageName}")
                .Attribute("hx-target", "#Content")
                .Attribute("hx-ext", "loading-state")
                .Attribute("hx-push-url", "true") 
                .Append("Edit")
                .ToHtmlString() +
            AllPages(wiki).ToHtmlString() +
            Title.Append(KebabToNormalCase(page.Name)).ToHtmlString()
            , HtmlMime);

    return pageName.Equals(HomePageName, StringComparison.OrdinalIgnoreCase)
        ? Results.Redirect("/new-page?pageName=home-page")
        : Results.NotFound($"Page {pageName} not found");

    static string KebabToNormalCase(string txt)
    {
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));
    }
});

app.MapGet("/all-pages", (Wiki wiki) => Results.Text(AllPages(wiki).ToHtmlString(), HtmlMime));

// Delete a page
app.MapPost("/delete-page", ([FromForm] string id, Wiki wiki) =>
{
    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning("Unable to delete page because form Id is missing");
        return Results.BadRequest("Unable to delete page because form Id is missing");
    }

    var (isOk, exception) = wiki.DeletePage(Convert.ToInt32(id), HomePageName);

    switch (isOk)
    {
        case false when exception is not null:
            app.Logger.LogError(exception, "Error in deleting page id {id}", id);
            return Results.Problem("Error in deleting page");
        case false:
            app.Logger.LogError("Unable to delete page id {id}", id);
            return Results.Problem("Unable to delete page");
    }

    return Results.Redirect($"/{HomePageName}");
});

app.MapPost("/delete-attachment", ([FromForm] string id, [FromForm] string pageId, Wiki wiki) =>
{
    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning("Unable to delete attachment because form Id is missing");
        return Results.BadRequest("Unable to delete attachment because form Id is missing");
    }

    if (StringValues.IsNullOrEmpty(pageId))
    {
        app.Logger.LogWarning("Unable to delete attachment because form PageId is missing");
        return Results.BadRequest("Unable to delete attachment because form PageId is missing");
    }

    var (isOk, page, exception) = wiki.DeleteAttachment(Convert.ToInt32(pageId), id);

    switch (isOk)
    {
        case false when exception is not null:
            app.Logger.LogError(exception, "Error in deleting page attachment id {id}", id);
            return Results.Problem("Error in deleting page attachment");
        case false:
            app.Logger.LogError("Unable to delete page attachment id {id}", id);
            return Results.Problem("Unable to delete page attachment");
    }

    return Results.Redirect($"/edit?pageName={page!.Name}");
});

// Add or update a wiki page
app.MapPost("/page",
    (IFormCollection inputFormCollection, [FromForm(Name = "Name")] string pageName, Wiki wiki) =>
    {
        var input = PageInput.From(inputFormCollection);

        var modelState = new ModelStateDictionary();
        var validator = new PageInputValidator(pageName, HomePageName);
        validator.Validate(input).AddToModelState(modelState, null);

        if (!modelState.IsValid)
            return Results.BadRequest(modelState.Select(x => x.Value!.Errors).SelectMany(x => x)
                .Select(x => x.ErrorMessage));
        // check if page exists
        var page = wiki.GetPage(pageName);
        if (page is not null && page.Id != input.Id)
            return Results.BadRequest("Page already exists refresh the page to edit it");

        var (isOk, p, ex) = wiki.SavePage(input);
        if (isOk) return Results.Redirect($"/{p!.Name}");

        app.Logger.LogError(ex, "Problem in saving page");
        return Results.Problem("Problem in saving page");
    }
);

await app.RunAsync();
return;

// End of the web part

static HtmlTag AllPages(Wiki wiki)
{
    return Div.Id("allPages")
        .Attribute("hx-swap-oob", "innerHTML")
        .Class("uk-width-1-5")
        .Append(Span.Class("uk-label").Append("Pages"))
        .Append(Ul.Class("uk-list")
            .Append(
                wiki.ListAllPages()
                    .OrderBy(x => x.Name)
                    .Select(x => Li.Append(
                            A.Attribute("hx-get", x.Name)
                                .Attribute("hx-target", "#Content")
                                .Attribute("hx-ext", "loading-states")
                                .Attribute("hx-swap", "innerHTML")
                                .Attribute("hx-push-url", "true")
                                .Append(x.Name)
                        )
                    )
            )
        );
}

static HtmlTag AllPagesForEditing(Wiki wiki)
{
    return Ul.Class("uk-list")
        .Append(wiki.ListAllPages().OrderBy(x => x.Name)
            .Select(x => Li.Append(Div.Class("uk-inline")
                    .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
                    .Append(Input.Text.Value($"[{KebabToNormalCase(x.Name)}](/{x.Name})")
                        .Class("uk-input uk-form-small").Style("cursor", "pointer")
                        .Attribute("onclick", "copyMarkdownLink(this);")
                    )
                )
            )
        );

    static string KebabToNormalCase(string txt)
    {
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));
    }
}

static string RenderMarkdown(string str)
{
    var sanitizer = new HtmlSanitizer();
    return sanitizer.Sanitize(Markdown.ToHtml(str,
        new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UseAdvancedExtensions().Build()));
}

static string RenderPageContent(Page page)
{
    return RenderMarkdown(page.Content);
}

static HtmlTag RenderDeletePageButton(Page page, AntiforgeryTokenSet antiForgery)
{
    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
    var id = Input.Hidden.Name("id").Value(page.Id.ToString());
    var submit = Div.Style("margin-top", "20px")
        .Append(Button.Class("uk-button uk-button-danger").Append("Delete Page"));

    var form = Form
        .Attribute("hx-post", "/delete-page")
        .Attribute("hx-target", "#Content")
        .Attribute("hx-ext", "loading-state")
        .Attribute("hx-swap", "innerHTML")
        .Attribute("hx-confirm", "Please confirm to delete this page")
        .Append(antiForgeryField)
        .Append(id)
        .Append(submit);

    return form;
}

static string RenderPageAttachmentsForEdit(Page page, AntiforgeryTokenSet antiForgery)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list");

    list = page.Attachments.Aggregate(list, (current, attachment) => current.Append(Li
        .Append(CreateEditorHelper(attachment))
        .Append(CreateDelete(page.Id, attachment.FileId, antiForgery))));

    return label.ToHtmlString() + list.ToHtmlString();

    static HtmlTag CreateDelete(int pageId, string attachmentId, AntiforgeryTokenSet antiForgery)
    {
        var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
        var id = Input.Hidden.Name("Id").Value(attachmentId);
        var name = Input.Hidden.Name("PageId").Value(pageId.ToString());

        var submit = Button.Class("uk-button uk-button-danger uk-button-small")
            .Append(Span.Attribute("uk-icon", "icon: close; ratio: .75;"));
        var form = Form
            .Style("display", "inline")
            .Attribute("hx-post", "/delete-attachment")
            .Attribute("hx-target", "#Content")
            .Attribute("hx-ext", "loading-state")
            .Attribute("hx-confirm", "Please confirm to delete this attachment")
            .Append(antiForgeryField)
            .Append(id)
            .Append(name)
            .Append(submit);

        return form;
    }

    HtmlTag CreateEditorHelper(Attachment attachment)
    {
        return Span.Class("uk-inline")
            .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
            .Append(Input.Text.Value($"[{attachment.FileName}](/attachment?fileId={attachment.FileId})")
                .Class("uk-input uk-form-small uk-form-width-large")
                .Style("cursor", "pointer")
                .Attribute("onclick", "copyMarkdownLink(this);")
            );
    }
}

static string RenderPageAttachments(Page page)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list uk-list-disc");
    list = page.Attachments.Aggregate(list,
        (current, attachment) =>
            current.Append(Li.Append(A.Href($"/attachment?fileId={attachment.FileId}").Append(attachment.FileName))));

    return label.ToHtmlString() + list.ToHtmlString();
}

// Build the wiki input form 
static string BuildForm(PageInput input, AntiforgeryTokenSet antiForgery,
    ModelStateDictionary? modelState = null)
{
    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);

    var nameField = Div
        .Append(Label.Class("uk-form-label").Append(nameof(input.Name)))
        .Append(Div.Class("uk-form-controls")
            .Append(Input.Text.Class("uk-input").Name("Name").Value(input.Name))
        );

    var contentField = Div
        .Append(Label.Class("uk-form-label").Append(nameof(input.Content)))
        .Append(Div.Class("uk-form-controls")
            .Append(Textarea.Name("Content").Class("uk-textarea").Append(input.Content))
        );

    var attachmentField = Div
        .Append(Label.Class("uk-form-label").Append(nameof(input.Attachment)))
        .Append(Div.Attribute("uk-form-custom", "target: true")
            .Append(Input.File.Name("Attachment"))
            .Append(Input.Text.Class("uk-input uk-form-width-large").Attribute("placeholder", "Click to select file")
                .ToggleAttribute("disabled", true))
        );

    if (modelState is not null && !modelState.IsValid)
    {
        if (IsFieldOk("Name"))
            nameField = modelState["Name"]!.Errors.Aggregate(nameField,
                (current, er) => current.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage)));

        if (IsFieldOk("Content"))
            contentField = modelState["Content"]!.Errors.Aggregate(contentField,
                (current, er) => current.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage)));
    }

    var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-primary").Append("Submit"));

    var form = Form
        .Class("uk-form-stacked")
        .Attribute("hx-post", "/page")
        .Attribute("hx-target", "#Content")
        .Attribute("hx-ext", "loading-state")
        .Attribute("hx-encoding", "multipart/form-data")
        .Append(antiForgeryField)
        .Append(nameField)
        .Append(contentField)
        .Append(attachmentField);

    if (input.Id is not null)
    {
        var id = Input.Hidden.Name("Id").Value(input.Id.ToString()!);
        form = form.Append(id);
    }

    form = form.Append(submit);

    return form.ToHtmlString();

    bool IsFieldOk(string key)
    {
        return modelState.ContainsKey(key) && modelState[key]!.ValidationState == ModelValidationState.Invalid;
    }
}

static string WikiPage(string page = HomePageName)
{
    return $$"""
             <!DOCTYPE html>
             <html lang="en">
             <head>
                 <meta charset="utf-8">
                 <meta name="viewport" content="width=device-width, initial-scale=1">
                 <title>home-page</title>
                 <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/css/uikit.min.css"/>
                 <link rel="stylesheet" href="https://unpkg.com/easymde/dist/easymde.min.css">
                 <script src="https://unpkg.com/easymde/dist/easymde.min.js"></script>
                 <script src="https://unpkg.com/htmx.org@2.0.0" integrity="sha384-wS5l5IKJBvK6sPTKa2WZ1js3d947pvWXbPJ1OmWfEuxLgeHcEbjUUA5i9V5ZkpCw" crossorigin="anonymous"></script>
                 <script src="https://unpkg.com/htmx-ext-loading-states@2.0.0/loading-states.js"></script>
                 <style>
                     .last-modified {
                         font-size: small;
                     }
             
                     a:visited {
                         color: blue;
                     }
             
                     a:link {
                         color: red;
                     }
                     [data-loading] {
                         display: none;
                     }
                 </style>
             </head>
             <body>
             <nav class="uk-navbar-container">
                 <div class="uk-container">
                     <div class="uk-navbar">
                         <div class="uk-navbar-left">
                             <ul class="uk-navbar-nav">
                                 <li class="uk-active"><a hx-get="/home-page" hx-push-url="/" hx-ext="loading-states" hx-target="#Content"><span uk-icon="home"></span></a></li>
                             </ul>
                         </div>
                         <div class="uk-navbar-center">
                             <div class="uk-navbar-item">
                                 <form hx-get="/new-page" hx-target="#Content" hx-ext="loading-states">
                                     <label class="uk-form-label" for="pageName">
                                         <input id="pageName" class="uk-input uk-form-width-large" type="text" name="pageName"
                                                placeholder="Type desired page title here">
                                     </label>
                                     <input type="submit" class="uk-button uk-button-default" value="Add New Page">
                                 </form>
                             </div>
                         </div>
                         <div class="uk-navbar-right">
                             <div uk-spinner data-loading ></div>
                         </div>
                     </div>
                 </div>
             </nav>

             <div class="uk-container " hx-get="/{{page}}" hx-trigger="load once" hx-ext="loading-states" hx-target="#Content">
                 <div uk-grid hx-history-elt>
                     <div id="Content" class="uk-width-4-5"></div>
                     
                     <div class="uk-width-1-5" id="allPages" hx-get="/all-pages" hx-trigger="every 1m [!isEditing()]" hx-swap="none"></div>
                 </div>
             </div>


             <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit.min.js"></script>
             <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit-icons.min.js"></script>
             <script>
                 var easyMDE;
                 
                 document.addEventListener("htmx:afterRequest", function (event) {
                     if (event.detail.xhr.status >= 400) {
                         UIkit.notification(event.detail.xhr.responseText, {status: 'danger'});
                         return;
                     }
                     if (event.detail.pathInfo.responsePath.includes("/new-page") || event.detail.pathInfo.responsePath.includes("/edit")) {
                         easyMDE = new EasyMDE({
                             insertTexts: {
                                 link: ["[", "]()"]
                             }
                         });
                     }
                 });
             
                 function copyMarkdownLink(element) {
                     element.select();
                     document.execCommand("copy");
                 }
                 
                 function isEditing() {
                     return document.getElementById("Content").querySelector("form") !== null;
                 }
             </script>

             </body>
             </html>
             """;
}

internal class Wiki(IWebHostEnvironment env, IMemoryCache cache, ILogger<Wiki> logger)
{
    private const string PageCollectionName = "Pages";
    private const string AllPagesKey = "AllPages";
    private const double CacheAllPagesForMinutes = 30;

    private readonly ILogger _logger = logger;

    private static DateTime Timestamp()
    {
        return DateTime.UtcNow;
    }

    // Get the location of the LiteDB file.
    private string GetDbPath()
    {
        return Path.Combine(env.ContentRootPath, "wiki.db");
    }

    // List all the available wiki pages. It is cached for 30 minutes.
    public List<Page> ListAllPages()
    {
        if (cache.Get(AllPagesKey) is List<Page> pages)
            return pages;

        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        var items = coll.Query().ToList();

        cache.Set(AllPagesKey, items,
            new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheAllPagesForMinutes)));
        return items;
    }

    // Get a wiki page based on its path
    public Page? GetPage(string path)
    {
        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        coll.EnsureIndex(x => x.Name);

        return coll.Query()
            .Where(x => x.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    // Save or update a wiki page. Cache(AllPagesKey) will be destroyed.
    public (bool isOk, Page? page, Exception? ex) SavePage(PageInput input)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            coll.EnsureIndex(x => x.Name);

            var existingPage = input.Id.HasValue ? coll.FindOne(x => x.Id == input.Id) : null;

            var sanitizer = new HtmlSanitizer();
            var properName = input.Name.Trim().Replace(' ', '-').ToLower();

            Attachment? attachment = null;
            if (!string.IsNullOrWhiteSpace(input.Attachment?.FileName))
            {
                attachment = new Attachment
                (
                    Guid.NewGuid().ToString(),
                    input.Attachment.FileName,
                    input.Attachment.ContentType,
                    Timestamp()
                );

                using var stream = input.Attachment.OpenReadStream();
                _ = db.FileStorage.Upload(attachment.FileId, input.Attachment.FileName, stream);
            }

            if (existingPage is null)
            {
                var newPage = new Page
                {
                    Name = sanitizer.Sanitize(properName),
                    Content = input
                        .Content, //Do not sanitize on input because it will impact some Markdown tag such as >. We do it on the output instead.
                    LastModifiedUtc = Timestamp()
                };

                if (attachment is not null)
                    newPage.Attachments.Add(attachment);

                coll.Insert(newPage);

                cache.Remove(AllPagesKey);
                return (true, newPage, null);
            }

            var updatedPage = existingPage with
            {
                Name = sanitizer.Sanitize(properName),
                Content = input
                    .Content, //Do not sanitize on input because it will impact some Markdown tag such as >. We do it on the output instead.
                LastModifiedUtc = Timestamp()
            };

            if (attachment is not null)
                updatedPage.Attachments.Add(attachment);

            coll.Update(updatedPage);

            cache.Remove(AllPagesKey);
            return (true, updatedPage, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "There is an exception in trying to save page name '{Name}'", input.Name);
            return (false, null, ex);
        }
    }

    public (bool isOk, Page? p, Exception? ex) DeleteAttachment(int pageId, string id)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            var page = coll.FindById(pageId);
            if (page is null)
            {
                _logger.LogWarning(
                    "Delete attachment operation fails because page id {id} cannot be found in the database", id);
                return (false, null, null);
            }

            if (!db.FileStorage.Delete(id))
            {
                _logger.LogWarning("We cannot delete this file attachment id {id} and it's a mystery why", id);
                return (false, page, null);
            }

            page.Attachments.RemoveAll(x => x.FileId.Equals(id, StringComparison.OrdinalIgnoreCase));

            var updateResult = coll.Update(page);

            if (updateResult) return (true, page, null);
            _logger.LogWarning(
                "Delete attachment works but updating the page (id {pageId}) attachment list fails", pageId);
            return (false, page, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex);
        }
    }

    public (bool isOk, Exception? ex) DeletePage(int id, string homePageName)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);

            var page = coll.FindById(id);

            if (page is null)
            {
                _logger.LogWarning("Delete operation fails because page id {id} cannot be found in the database", id);
                return (false, null);
            }

            if (page.Name.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Page id {id}  is a home page and delete operation on home page is not allowed", id);
                return (false, null);
            }

            //Delete all the attachments
            foreach (var a in page.Attachments) db.FileStorage.Delete(a.FileId);

            if (coll.Delete(id))
            {
                cache.Remove(AllPagesKey);
                return (true, null);
            }

            _logger.LogWarning("Somehow we cannot delete page id {id} and it's a mystery why.", id);
            return (false, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    // Return null if file cannot be found.
    public (LiteFileInfo<string> meta, byte[] file)? GetFile(string fileId)
    {
        using var db = new LiteDatabase(GetDbPath());

        var meta = db.FileStorage.FindById(fileId);
        if (meta is null)
            return null;

        using var stream = new MemoryStream();
        db.FileStorage.Download(fileId, stream);
        return (meta, stream.ToArray());
    }
}

internal record Page
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public DateTime LastModifiedUtc { get; init; }

    public List<Attachment> Attachments { get; init; } = [];
}

internal record Attachment(
    string FileId,
    string FileName,
    string MimeType,
    DateTime LastModifiedUtc
);

internal record PageInput(int? Id, string Name, string Content, IFormFile? Attachment)
{
    public static PageInput From(IFormCollection form)
    {
        var (id, name, content) = (form["Id"], form["Name"], form["Content"]);

        int? pageId = null;

        if (!StringValues.IsNullOrEmpty(id))
            pageId = Convert.ToInt32(id);

        var file = form.Files["Attachment"];

        return new PageInput(pageId, name!, content!, file);
    }
}

internal class PageInputValidator : AbstractValidator<PageInput>
{
    public PageInputValidator(string pageName, string homePageName)
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
        if (pageName.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            RuleFor(x => x.Name).Must(name => name.Equals(homePageName))
                .WithMessage($"You cannot modify home page name. Please keep it {homePageName}");

        RuleFor(x => x.Content).NotEmpty().WithMessage("Content is required");
    }
}