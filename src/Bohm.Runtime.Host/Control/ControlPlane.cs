using LocalOrigin.Storage;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Adoption;
using Bohm.Runtime.Host.Llm;
using Bohm.Runtime.Pages;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Control;

/// <summary>
/// The API the process that started the runtime uses to drive it — the desktop shell, or a
/// headless deployment's configuration. It answers only on the loopback address itself (never on
/// an application origin) and only to requests bearing the per-launch secret.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>GET /__control/runtime</c></term><description>Facts about this runtime: <c>contract</c>, the edition of the application contract it serves (<see cref="AppContract"/>).</description></item>
/// <item><term><c>GET /__control/apps</c></term><description>Adopted applications, oldest first, each with the last day it was used; an unsaved result says so, with when it was left and when it expires.</description></item>
/// <item><term><c>GET /__control/apps/unreadable</c></term><description>Application folders that could not be read — kind <c>cannotOpen</c> (the system could not open a file right now, e.g. kept only in the cloud while offline), <c>damaged</c>, or <c>interruptedRemoval</c> (a removal for good stopped before the recycle bin; the folder is still there). Nothing in them is changed; one unreadable application never hides the others.</description></item>
/// <item><term><c>POST /__control/apps/matches</c></term><description>Earlier adoptions of the HTML in the body, of a file at the same path (optional <c>X-Bohm-Original-Path</c>), or of a file beside it under the same name but for a browser's download number, each with how it matches.</description></item>
/// <item><term><c>POST /__control/apps</c></term><description>Adopts the HTML in the body (optional <c>X-Bohm-Original-Path</c>, URL-encoded).</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions</c></term><description>Takes in the HTML in the body as a new revision of the application: same application, same data, new code (optional <c>X-Bohm-Original-Path</c>). Pages still running the old code can no longer write.</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions/from-package</c></term><description>Takes in the code of the package (<c>.bohm</c>) at the full path in the body — the page of the revision in use there, after the checks taking a package in runs — as a new revision of this application, its data here unchanged (as <c>revisions</c> does; the package's data is not used). 400 <c>{ reason }</c> as for taking a package in; 409 when the package holds another application, or its code is the revision already in use.</description></item>
/// <item><term><c>POST /__control/apps/{id}/archive</c> · <c>/restore</c></term><description>Puts the application away or brings it back. Only a mark on its record changes — code, data, revisions and usage record stay; an archived application is not served. The caller closes its pages first.</description></item>
/// <item><term><c>POST /__control/results</c></term><description>Makes a page out of an answer the person was given — <c>{ title, text, sources?: [{ name, url? }], lang?, sourcesHeading? }</c> — and adds it as an unsaved result (201, the application). The page is the text as read, escaped, with its sources; no script. 400 when the body is not that shape.</description></item>
/// <item><term><c>POST /__control/apps/{id}/keep</c></term><description>Keeps an unsaved result: it becomes one of the person's applications, with its data. 404 for an unknown id.</description></item>
/// <item><term><c>POST /__control/apps/{id}/left</c></term><description>The person left an unsaved result (closed its tab): its retention counts from now, and serving its page again ends it. Nothing changes for a saved application.</description></item>
/// <item><term><c>POST /__control/apps/{id}/export</c></term><description>Copies the application's folder, as it is, to the new folder whose full path is the body — the exchange format is the folder itself. The data is checkpointed first; the original is unchanged. 409 when something with that name is already there or its parent is missing.</description></item>
/// <item><term><c>POST /__control/apps/{id}/export?data=leave</c></term><description>Copies only what makes the application — its record, its code in every revision, the code it loads from other hosts and its sources' rules — leaving out its data, what its sources read, its usage record and the permissions to read. Whoever takes it in starts with none of them.</description></item>
/// <item><term><c>POST /__control/apps/{id}/package?data=none|all</c></term><description>Writes the application into a package (<c>.bohm</c>) at the full path in the body: a zip of <c>manifest.json</c> and the folder an export with the same data makes, each recorded original path shortened to its file name. <c>data</c> is required. Answers <c>{ id, path, version, contentSha256, data, includes, permissions }</c> — <c>version</c> goes up by one when the current code differs from the last package's. 400 for a path that is not full or does not end in <c>.bohm</c>, or a missing <c>data</c>; 409 when something with that name is already there or its folder is missing.</description></item>
/// <item><term><c>GET /__control/apps/{id}/sources</c></term><description>The web pages the application reads: <c>[{ name, rule: { site, selector, columns }, grant: { site, grantedAt } | null, lastReadAt }]</c>, in the order declared.</description></item>
/// <item><term><c>PUT /__control/apps/{id}/sources/{name}</c></term><description>Declares a source from <c>{ rule: { site, selector, columns }, granted }</c> — <c>granted</c> when the person has just allowed the site to be read — or replaces its rule; readings already kept stay. Names are lowercase letters, digits and hyphens. 400 for a bad name or rule.</description></item>
/// <item><term><c>POST /__control/apps/{id}/sources/{name}/readings</c></term><description>Keeps what was read, <c>{ source, columns, rows: [[cell, …]] }</c>, and answers it as the application will read it. Refused, with nothing kept: 403 <c>{ code: "not-granted" | "outside-grant" }</c> without permission or for a page not under the permitted site; 409 <c>{ code: "shape-mismatch", columns }</c> when the columns are not the rule's, in order, or a row lacks a cell. The application reads it at <c>/__bohm/sources/{name}</c> (see <see cref="SourcesServing"/>).</description></item>
/// <item><term><c>DELETE /__control/apps/{id}/sources/{name}/grant</c></term><description>Takes back the permission to read the source; its rule and readings stay.</description></item>
/// <item><term><c>GET /__control/apps/page-targets</c></term><description>The applications a web page can be sent to — those whose manifest declares a <c>share_target</c> (GET) — in the catalog's order: <c>[{ id, params: { title, text, url } }]</c>, each param the query name the manifest gives it, or <c>null</c>. Archived applications are not among them.</description></item>
/// <item><term><c>POST /__control/apps/{id}/pages</c></term><description>Keeps a web page the person sent to the application, <c>{ url, title, text, html, lang, byline }</c> — the shell read it and cleaned its HTML — and answers <c>{ page: { id, receivedAt, url, title, lang, byline, excerpt }, withoutHtml }</c>; the HTML is left out when text and HTML together pass 2 MB. 400 without a web address or text; 413 when the text alone passes 2 MB. The application reads it at <c>/__bohm/pages/{id}</c> (see <see cref="PagesServing"/>).</description></item>
/// <item><term><c>POST /__control/packages/inspect</c></term><description>Checks the package (<c>.bohm</c>) at the full path in the body exactly as taking it in would — and takes nothing in: <c>{ manifest, alreadyHere, sameCode, sameCodeInUse, compatibility }</c>, so the person can see what the application asks to do before it lands. <c>compatibility</c> lists what in its page will not work as written: <c>online-database</c>, <c>indexeddb</c>, <c>session-storage</c>, <c>cookies</c> (data kept where it does not stay) or <c>outside-data</c> (requests to an outside server other than an AI service). 400 for a path that is not full, or <c>{ reason }</c> as for taking it in.</description></item>
/// <item><term><c>POST /__control/apps/import</c></term><description>Takes in the exported application folder whose full path is the body, as it is — same identity, data, revisions and usage record — or the package (<c>.bohm</c>) at that path, after checking every file against its manifest. With <c>?as=separate</c> (a package only) the package's application is taken in separately — under a new identity, named as the package names it (numbered when another application has that name), with <c>forkedFrom</c> — so an application already here stays and both are kept. 400 when it is not an application folder, or for a package <c>{ reason }</c> — <c>not-a-package</c>, <c>unknown-format</c>, <c>damaged</c> or <c>too-large</c> (it unpacks to more than the drive has room for); 409 when the application is already here (nothing is replaced) — for a package <c>{ id, sameCode, sameCodeInUse }</c>: whether its code is one of the revisions here, and the one in use.</description></item>
/// <item><term><c>DELETE /__control/apps/{id}</c></term><description>Removes an archived application, or an unsaved result, for good: its folder goes to the recycle bin (the operating system's way back); its usage record stays and keeps appearing in the usage report with the day it was removed. 409 when the application is not archived.</description></item>
/// <item><term><c>POST /__control/apps/{id}/proposals</c></term><description>Proposes a change to the application's current source: the body is <c>{ instruction, target: { html, text? } }</c> — what the person asked and the element they pointed at. With <c>broken: { html, problems: [text, …] }</c> the change starts from that version instead — the earlier proposal for this request, which failed when the shell opened it with a copy of the data — and the model is told the problems (the first ten) to fix them while keeping the change; 400 when it has no HTML or no problem. Answers <c>{ html, summary, edits: [{ old, new }], model, stopped }</c> — <c>stopped</c> is <c>output-limit</c> when the model's answer reached its length limit after these edits, or <c>step-limit</c> when it used all its rounds of reading and replacing, so they may not be all it meant to make; nothing is applied (taking it in is a new revision). Made with the model chosen for proposals (<c>/__control/edit/model</c>). 409 with what is missing (<c>{ needs: "localModel" | "key", provider }</c>), 503 with why when the model cannot run or stops — <c>{ detail, provider?, stopped? }</c>, <c>stopped</c> being <c>output-limit</c> when the answer reached its length limit before any change, or <c>step-limit</c> when the model used all its rounds before one.</description></item>
/// <item><term><c>POST /__control/apps/proposals</c></term><description>Proposes a new application — from what the person asked alone, or from an answer and the tables on the pages behind it: the body is <c>{ question, answer?, lang?, pages?: [{ url, title?, tables: [{ selector, headers, rows, preview }] }] }</c> — the tables as the shell found them; with no pages the application is what the person asked for, reading no source. With <c>broken: { html, problems: [text, …] }</c> (no pages) the earlier proposal for that request, which failed when the shell opened it, is fixed from that version with exact replacements and the problems told (the first ten), and held to the same checks; 400 with pages, or without its HTML or a problem. Answers <c>{ title, html, sources: [{ name, page, rule: { site, selector, columns } }], summary, model, refused: [reason, …] }</c> — <c>refused</c> holds what was sent back to the model before the proposal was kept; nothing is kept. What was asked alone may instead be one thing to do now about the pages open in front of the person (summarize them, compare them), which an application could not do: then the answer is <c>{ task, model }</c> — <c>task</c> the model's reason — and no application, for the caller to do it as a web question. The rules are made from the tables and columns the model chose among those given, and the application is refused unless it reads exactly the sources it declares and puts their values on the page only as text. Made with the model chosen for proposals, but never the one on this computer — 409 <c>{ needs: "largerModel" }</c> — or 409 with what is missing, 503 with why when the model cannot run, stops or proposes nothing usable — <c>{ detail, provider?, stopped? }</c>, <c>stopped</c> being <c>output-limit</c> when the answer reached its length limit first.</description></item>
/// <item><term><c>POST /__control/previews</c></term><description>Holds a proposed new application for a look, from <c>{ html, readings: { name: { source, columns, rows } } }</c>: answers <c>{ token, origin, shareTarget }</c>, served there with no data and each source answering the rows given, until two minutes after it was last used. <c>shareTarget</c> is the query names its manifest gives a shared page (<c>{ title, text, url }</c>, as for <c>apps/page-targets</c>), or <c>null</c> when it receives none. Nothing is kept. <c>GET /__control/previews/{token}</c> and <c>DELETE</c> as for an application's previews.</description></item>
/// <item><term><c>POST /__control/previews/{token}/pages</c></term><description>Sends a web page to a proposed new application's preview, to try it with — the body and answer as for <c>apps/{id}/pages</c>. The page is held with the preview, which reads it at <c>/__bohm/pages/{id}</c>, and goes with it; once one has arrived the preview's calls to a model are relayed as the application's would be (before, they are declined). 404 once the preview has expired or been removed.</description></item>
/// <item><term><c>POST /__control/apps/promotions</c></term><description>Takes in a proposed application — <c>{ html, title?, sources?: [{ name, rule }], readings?: { name: { source, columns, rows } } }</c> — with its sources, if any, allowed (taking it in is the person's permission) and the rows read for them kept as the first readings. 201 with the application. Everything is checked first: a bad rule, rows for no source, or rows its source would refuse leave nothing behind (400).</description></item>
/// <item><term><c>POST /__control/apps/{id}/previews</c></term><description>Holds the HTML in the body as a preview of a new revision, for a look before it is taken in: answers <c>{ token, origin }</c> — the preview is served at that origin (never the application's own) with the application's current data to read and nowhere to write it, until two minutes after it was last used. Nothing about the application changes.</description></item>
/// <item><term><c>GET /__control/apps/{id}/previews/{token}</c></term><description>What went wrong while the preview loaded: <c>{ errors, blocked, askedModel }</c> — errors thrown, with lines as in the previewed document, what the content security policy refused (<c>category host</c>), and whether it called a model — declined in a preview, so errors that followed may not happen once it is taken in. 404 once it has expired or been removed.</description></item>
/// <item><term><c>DELETE /__control/apps/{id}/previews/{token}</c></term><description>Stops serving the preview.</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions/revert</c></term><description>Goes back to the previous revision, code and data together; what the revision being left wrote is kept aside.</description></item>
/// <item><term><c>GET /__control/apps/{id}/revisions</c></term><description>The application's revisions, oldest first: number, the one before it, when it was taken in, the name of the file it came from (none for an applied change), whether it is in use, and — for one the application was put back from — where the data it wrote stands against the data now.</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions/{n}/import</c></term><description>Takes the data revision <c>n</c> wrote back in, replacing the data now — only while nothing was written since going back and the code in use reads its keys; 409 otherwise. <c>…/undo-import</c> puts back the data it replaced, while the data is still what was taken in.</description></item>
/// <item><term><c>GET /__control/apps/{id}/imports</c></term><description>What a table file could be imported into — <c>{ collections: [{ collection, records, fields: [{ name, kind }], identity }], imports: [{ number, file, collection, added, replaced, skipped, invalid, takenAt, undone }], fileTypes: [extension | "*"] }</c>: every stored key holding a list of records, with the fields and kinds the records show (a list with no records yet is not one) and the field earlier imports told the same record apart by; the imports so far, oldest first; and the file extensions the application's own file inputs take ("*" for one that takes any — see <see cref="TableImports.AppFileInputs"/>). Headers the person put into a field of another name in an earlier import match that field again.</description></item>
/// <item><term><c>POST /__control/apps/{id}/imports/preview</c></term><description>What importing a table file would do, from <c>{ file: { name, content, codePage? }, collection, identity?, sameRecord?, columns? }</c> (see <see cref="Adoption.TableImportEndpoints"/>; <c>codePage</c> — the code page the person chose for a file that is not UTF-8, else this computer's double-byte one is tried): <c>{ columns: [{ column, field }], unfilled, added, replaced, skipped, invalid: [{ row, field, cell }], sample, generated, codePage }</c> — <c>codePage</c> the file was read in, null for UTF-8 — <c>cell</c> is the cell that is not its field's kind, null for a required field left empty; <c>generated</c> is the application's own key the runtime fills for new records (none: null) — a column whose field is null is left out. Nothing is written. 400 <c>{ problem }</c> — the file's (<c>not-utf8</c>, <c>empty</c>, <c>unclosed-quote</c>, <c>too-large</c>, <c>too-many-rows</c>, <c>duplicate-header</c>) or the choice's (<c>unknown-collection</c>, <c>unknown-identity</c>, <c>unknown-field</c>, <c>unknown-same-record</c>).</description></item>
/// <item><term><c>POST /__control/apps/{id}/imports</c></term><description>Does it, with the same body: the data before and after are kept aside, the rows go in as one write, and pages loaded before reload (201 with the import's record). 409 when no row would go in.</description></item>
/// <item><term><c>POST /__control/apps/{id}/imports/{n}/undo</c></term><description>Puts the data back to how it was before import <c>n</c> — only while it is still what the import left; 409 when something was written since or it was undone already.</description></item>
/// <item><term><c>GET /__control/apps/{id}/usage</c></term><description>The application's usage record: each recorded day's signals and load failures, its revisions, its first and last day of use and where it stands against the 30-day retention rule. Days are local; nothing leaves this computer.</description></item>
/// <item><term><c>GET /__control/usage-report</c></term><description>Every application's usage record in one document the person can read and choose to hand over: application ids, days, signals, revisions and retention — no names, paths or content. Nothing is sent; the caller decides what happens to it.</description></item>
/// <item><term><c>GET /__control/apps/{id}/tabs/{tab}</c></term><description>The highest write sequence applied from one loaded page (<c>ack</c>) and the highest sequence the page reported having issued (<c>issued</c>); <c>left</c> once the page's report sent after leaving has arrived, which makes <c>issued</c> final. A host closing the page waits until <c>ack</c> reaches both the sequence it read before the page left and <c>issued</c>; 404 when the page is not (or no longer) the application's.</description></item>
/// <item><term><c>POST /__control/apps/{id}/loss-suspected</c></term><description>Records that a closing page of the application went away before its last writes could be confirmed as applied. Counted per day in the usage record.</description></item>
/// <item><term><c>GET /__control/apps/{id}/status</c></term><description>Today's usage signals, load failures, blocked resources, missing files, calls to a server the application expected (method and path) and keys needed.</description></item>
/// <item><term><c>POST /__control/apps/{id}/assets</c></term><description>Fetches (again) the code the application loads from other hosts; answers what was and was not cached.</description></item>
/// <item><term><c>GET /__control/web/robots?url=</c></term><description>Whether the browser's agent may open the web address <c>url</c> on its own, by the site's robots.txt (RFC 9309, product token <c>Bohm-Agent</c>, else the <c>*</c> rules): <c>{ allowed, reason, rule, robotsUrl, crawlDelay }</c> — <c>reason</c> is <c>disallowed</c>, <c>server-error</c> or <c>unreachable</c> when it may not (a missing file allows everything; a server error or no answer allows nothing), <c>rule</c> the line that decided it. Read once a day per origin (a failure for five minutes), without cookies, counted as fetched from the site's host. What a person opens is not asked here. 400 for an address that is not http(s).</description></item>
/// <item><term><c>GET /__control/egress</c></term><description>What left this computer since the runtime started: sent, fetched and blocked, by host.</description></item>
/// <item><term><c>GET /__control/edit/model</c></term><description>The model proposals are made with: <c>{ provider, model, missing }</c> — no provider for the model on this computer (the default); <c>missing</c> says what must be connected first.</description></item>
/// <item><term><c>PUT /__control/edit/model</c></term><description>Chooses a connected provider's model for proposals, from <c>{ provider, model }</c>, and remembers it. The application's source then goes to that provider with each proposal, counted as sent. 400 for an unknown provider or no model name.</description></item>
/// <item><term><c>DELETE /__control/edit/model</c></term><description>Goes back to the model on this computer.</description></item>
/// <item><term><c>GET /__control/llm</c></term><description>AI providers, whether a key is connected (never the key) and what, without one, answers the provider's chat requests: <c>company</c> (the organization's model server), <c>local</c> (the model on this computer) or nothing.</description></item>
/// <item><term><c>PUT /__control/llm/{provider}/key</c></term><description>Connects the key in the body, stored in the vault.</description></item>
/// <item><term><c>DELETE /__control/llm/{provider}/key</c></term><description>Disconnects it.</description></item>
/// <item><term><c>GET /__control/llm/local-model</c></term><description>The model on this computer that answers when no key is connected: its file, whether it is loaded or loading, why the last load failed, and whether it was fixed at start.</description></item>
/// <item><term><c>PUT /__control/llm/local-model</c></term><description>Chooses the model file named in the body (a full path to a <c>.gguf</c> file) and remembers it; 400 when there is no such file, 409 when fixed at start.</description></item>
/// <item><term><c>DELETE /__control/llm/local-model</c></term><description>Chooses none.</description></item>
/// <item><term><c>GET /__control/llm/local-model/catalog</c></term><description>The models the model library knows by a short name, to get onto this computer: <c>[{ id, name, description, parameters, license, licenseTier, sizeBytes, downloaded }]</c> — read without the network.</description></item>
/// <item><term><c>POST /__control/llm/local-model/describe</c></term><description>What getting the model named in the body (a catalog id or a Hugging Face repository) would take, before it starts: <c>{ result, model, name, license, licenseTier, sizeBytes, downloaded }</c> — <c>result</c> is <c>ok</c>, <c>not-found</c> or <c>unreachable</c>; the licence when the catalog knows the model. Asks the model host once — counted as sent to it.</description></item>
/// <item><term><c>POST /__control/llm/local-model/download</c> · <c>DELETE</c></term><description>Makes the model named in the body the model in use: at once when it is on this computer already (200 — nothing is downloaded or sent, and <c>download</c> is null), otherwise by getting it in the background (202 — progress is the <c>download</c> of <c>GET /__control/llm/local-model</c>: <c>{ model, name, bytes, total, failure }</c>; when it arrives it is the model in use and <c>download</c> is null, and the download is counted as sent to the model host). <c>DELETE</c> stops the one under way. 409 when one is under way or the model was fixed at start.</description></item>
/// <item><term><c>POST /__control/llm/local-model/load</c></term><description>Starts loading the chosen model now instead of on the first request (202 with the model's state — <c>loading</c> until it is loaded or <c>error</c> says why not); 409 when no model is chosen.</description></item>
/// <item><term><c>GET /__control/llm/company-model</c></term><description>The organization's model server: <c>{ endpoint, model, fixed, keyConnected, contextWindow, maxTokens, reasoning, reportedContextWindow }</c> — <c>endpoint</c> and <c>model</c> are null when none is set; <c>fixed</c> when it was set at start; the model's limits as they were set, each null when unknown; <c>reportedContextWindow</c> the context window the server's model list reported for the model (vLLM's <c>max_model_len</c>) once it was asked — before the first proposal or question, or by a check — used when <c>contextWindow</c> is not set, null otherwise.</description></item>
/// <item><term><c>PUT /__control/llm/company-model</c></term><description>Sets the server from <c>{ endpoint, model, contextWindow?, maxTokens?, reasoning? }</c> — its OpenAI-compatible base address (http or https), a model's name and what is known of the model's limits (left out: unknown) — and remembers it; 400 when any is not usable, 409 when fixed at start.</description></item>
/// <item><term><c>DELETE /__control/llm/company-model</c></term><description>Sets none; 409 when fixed at start.</description></item>
/// <item><term><c>POST /__control/llm/company-model/check</c></term><description>Asks the server once for its models, with the key when one is connected: <c>{ result, status, modelListed, reportedContextWindow, unreached }</c> — <c>result</c> is <c>answers</c>, <c>key-refused</c> (401 or 403), <c>not-found</c> (404 — often a base address without its <c>/v1</c>), <c>refused</c> (another status) or <c>unreachable</c> (no answer within 10 seconds), and then <c>unreached</c> says why when the failure does: <c>host-not-found</c>, <c>connection-refused</c> or <c>no-answer</c>, null otherwise; <c>modelListed</c> whether its model list names the model it was set with, null when it gave no such list; when it does and no context window was set, the server is asked for the model's (as <c>reportedContextWindow</c> — null when it says none). 404 when no server is set. Counted as sent to the server's host.</description></item>
/// <item><term><c>POST /__control/llm/company-model/models</c></term><description>Asks a server for the models it serves, so a name is picked rather than typed — at <c>{ endpoint?, key? }</c>'s address (the same rules as setting one), or the server in use when none is given (with the connected key when no key is given): <c>{ result, status, models: [{ id, contextWindow }], unreached }</c> — <c>result</c> as for <c>check</c>; <c>models</c> sorted by id, each <c>contextWindow</c> what the server reports (null when it says none), empty unless it <c>answers</c> with a list. 400 when the address is not usable, 404 when none is given and no server is set. Counted as sent to the server's host.</description></item>
/// <item><term><c>PUT /__control/llm/company-model/key</c> · <c>DELETE</c></term><description>Connects the key in the body for the server, stored in the vault, or disconnects it. Many servers want none.</description></item>
/// <item><term><c>GET /__control/agent/model</c> · <c>PUT</c> · <c>DELETE</c></term><description>The model questions about web pages go to, the same way as <c>/__control/edit/model</c>: by default the organization's model server or the model on this computer; a connected provider's model only when the person chooses one — the pages' text then goes to that provider, counted as sent.</description></item>
/// <item><term><c>POST /__control/agent/turns</c></term><description>One turn of a question about the open web pages: the body is the whole conversation, <c>{ messages: [ { role: "user", text } | { role: "assistant", text?, toolCalls } | { role: "tool", toolCallId, text, notice? } ] }</c>, ending with the question or with the results of the calls the last turn asked for; a tool message's <c>notice</c> is the caller's own word about the call (what the browser did and why — never page text), shown to the model after the result, outside the page material. Answers <c>{ status: "done", text, model, stopped }</c> — <c>stopped</c> is <c>output-limit</c> when the answer reached the model's length limit, so it may be cut short — or <c>{ status: "requires_action", text?, toolCalls: [{ id, name, arguments }], model }</c> — calls to the browser tools (<c>list_tabs</c>, <c>read_page</c>, <c>snapshot_page</c>, <c>click</c>, <c>type</c>, <c>press_key</c>, <c>navigate</c>, <c>go_back</c>, <c>make_page</c>) for the caller to make and send back; the caller runs them, and asks the person before a click or an Enter that cannot be taken back. Nothing is kept between turns. Asked with <c>Accept: application/x-ndjson</c>, the answer comes as it is written: one JSON object per line — <c>{ text }</c> for each piece of the model's text, then that same turn object, or <c>{ status: "failed", detail, provider }</c> — what a 503 would carry — when the model cannot finish: the status is sent before the model is asked. What is missing is still a 409. Asked of the model chosen at <c>/__control/agent/model</c>; 409 with what is missing (<c>{ needs: "localModel" | "key", provider }</c>), 503 with why when it cannot run or stops.</description></item>
/// <item><term><c>POST /__control/drain</c></term><description>Waits until no storage write is in progress.</description></item>
/// <item><term><c>POST /__control/shutdown</c></term><description>Drains, then stops the runtime.</description></item>
/// </list>
/// </remarks>
internal static partial class ControlPlane
{
    public const string PathPrefix = "/__control";
    public const string OriginalPathHeader = "X-Bohm-Original-Path";

    /// <summary>
    /// On a new revision made by a change, the person's request for it — percent-encoded like
    /// <see cref="OriginalPathHeader"/>. It stays on this computer: a package of the application does not carry it.
    /// </summary>
    public const string RequestHeader = "X-Bohm-Request";

    /// <summary>How long no write may be in progress before the runtime counts as drained.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest a drain waits. Past it the caller proceeds and a loss is possible.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    public static bool IsControlHost(HttpRequest request) =>
        request.Host.Host is "127.0.0.1" or "localhost" && request.Path.StartsWithSegments(PathPrefix);

    public static async Task HandleAsync(HttpContext context, string? secret)
    {
        var request = context.Request;
        var response = context.Response;
        if (secret is null || !Authorized(request, secret))
        {
            response.StatusCode = secret is null ? StatusCodes.Status404NotFound : StatusCodes.Status401Unauthorized;
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        var segments = request.Path.Value![PathPrefix.Length..].Trim('/').Split('/');
        var port = request.Host.Port ?? 80;
        var cancel = context.RequestAborted;

        switch (request.Method, segments)
        {
            case ("GET", ["apps"]):
                // The usage record is read from its file: appends reach it at once, and reading it does not open the application.
                await WriteAsync(response, (await ListAsync(catalog, port, cancel).ConfigureAwait(false)).Apps, cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", "page-targets"]):
                var targets = new List<PagesServing.PageTarget>();
                foreach (var listed in (await catalog.ReadListingAsync(cancel).ConfigureAwait(false)).Apps)
                {
                    if (listed.ArchivedAt is not null) continue;
                    try
                    {
                        if (ShareTarget.Find(Encoding.UTF8.GetString(await catalog.ReadHtmlAsync(listed.Id, cancel).ConfigureAwait(false))) is { } shareTarget)
                            targets.Add(new PagesServing.PageTarget(listed.Id, shareTarget));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // An application whose code cannot be read now is not offered; the catalog reports it elsewhere.
                    }
                }

                await response.WriteAsJsonAsync(targets, PagesHttpJson.Default.ListPageTarget, cancellationToken: cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", "unreadable"]):
                await WriteAsync(response, (await ListAsync(catalog, port, cancel).ConfigureAwait(false)).Unreadable, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", "matches"]):
                var candidate = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                var matches = await catalog.FindEarlierAdoptionsAsync(candidate, OriginalPath(request), cancel).ConfigureAwait(false);
                var views = new List<MatchView>();
                foreach (var m in matches) views.Add(await ViewMatchAsync(catalog, m, port, cancel).ConfigureAwait(false));
                await WriteAsync(response, views, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps"]):
                var html = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                if (html.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var adopted = await catalog.AdoptAsync(html, OriginalPath(request), cancellationToken: cancel).ConfigureAwait(false);
                if (context.RequestServices.GetRequiredService<RuntimeHostOptions>().FetchAssetsOnAdoption)
                    context.RequestServices.GetRequiredService<AssetFetcher>().Start(adopted.Id);
                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(adopted, port, canRevert: false), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["results"]):
                if (ResultPage.Read(await ReadBodyAsync(request, cancel).ConfigureAwait(false)) is not { } page)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var result = await catalog.AdoptAsync(page.Render(), originalPath: null, unsaved: true, page.Title, cancel).ConfigureAwait(false);
                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(result, port, canRevert: false), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["registries", "read"]):
                await ReadRegistryAsync(context, catalog, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["registries", "publish"]):
                await PublishToRegistryAsync(context, catalog, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["registries", "inspect"]):
                await InspectFromRegistryAsync(context, catalog, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["registries", "install"]):
                await InstallFromRegistryAsync(context, catalog, port, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["packages", "inspect"]):
                var inspected = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (!Path.IsPathFullyQualified(inspected))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                try
                {
                    await WriteAsync(response, await catalog.InspectPackageAsync(inspected, AiServices.Values, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
                }
                catch (InvalidPackageException e)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteAsync(response, Refusal(e), cancel).ConfigureAwait(false);
                }

                break;

            case ("POST", ["apps", "import"]):
                var source = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (!Path.IsPathFullyQualified(source))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var isPackage = source.EndsWith(AppPackage.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(source);
                var separately = request.Query["as"].ToString() switch { "" => false, "separate" => true, _ => (bool?)null };
                if (separately is null || (separately == true && !isPackage))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                AdoptedApp imported;
                try
                {
                    imported = !isPackage ? await catalog.ImportAsync(source, cancel).ConfigureAwait(false)
                        : separately == true ? await catalog.ImportPackageSeparatelyAsync(source, cancel).ConfigureAwait(false)
                        : await catalog.ImportPackageAsync(source, cancel).ConfigureAwait(false);
                }
                catch (InvalidPackageException e)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteAsync(response, Refusal(e), cancel).ConfigureAwait(false);
                    break;
                }
                catch (AppAlreadyHereException e)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    await WriteAsync(response, new AlreadyHereView(e.Id, e.SameCode, e.SameCodeInUse), cancel).ConfigureAwait(false);
                    break;
                }
                catch (InvalidDataException)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(imported, port, await catalog.CanRevertAsync(imported, cancel).ConfigureAwait(false), catalog.OpenUsage(imported.Id).LastUsedOn), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var fromPackageId, "revisions", "from-package"]):
                if (await catalog.GetAsync(fromPackageId, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var packageFile = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (!Path.IsPathFullyQualified(packageFile))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                byte[] packagePage;
                try
                {
                    var (packageManifest, packedPage) = await catalog.ReadPackagePageAsync(packageFile, cancel).ConfigureAwait(false);
                    if (packageManifest.Id != fromPackageId)
                    {
                        response.StatusCode = StatusCodes.Status409Conflict;
                        break;
                    }

                    packagePage = packedPage;
                }
                catch (InvalidPackageException e)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteAsync(response, Refusal(e), cancel).ConfigureAwait(false);
                    break;
                }

                await ChangeRevisionAsync(context, fromPackageId, StatusCodes.Status201Created,
                    storage => catalog.ReviseAsync(fromPackageId, packagePage, packageFile, storage, cancellationToken: cancel), app => app.Usage.RecordRevision(reverted: false)).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var revisedId, "revisions"]):
                if (await catalog.GetAsync(revisedId, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var revisedHtml = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                if (revisedHtml.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var revisedPath = OriginalPath(request);
                var revisedFor = Escaped(request, RequestHeader);
                await ChangeRevisionAsync(context, revisedId, StatusCodes.Status201Created,
                    storage => catalog.ReviseAsync(revisedId, revisedHtml, revisedPath, storage, revisedFor, cancel), app => app.Usage.RecordRevision(reverted: false)).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var restoredId, "revisions", var restoreTo, "restore"]):
                if (await catalog.GetAsync(restoredId, cancel).ConfigureAwait(false) is null
                    || !int.TryParse(restoreTo, NumberStyles.None, CultureInfo.InvariantCulture, out var restoreRevision)
                    || !(await catalog.ListRevisionsAsync(restoredId, cancel).ConfigureAwait(false)).Any(r => r.Revision == restoreRevision))
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                // Going back to an earlier revision's code is a new revision: the data stays as it is, «revert» undoes it.
                await ChangeRevisionAsync(context, restoredId, StatusCodes.Status201Created,
                    storage => catalog.RestoreAsync(restoredId, restoreRevision, storage, cancel), app => app.Usage.RecordRevision(reverted: true)).ConfigureAwait(false);
                break;

            case ("POST", ["agent", "turns"]):
                await AgentTurnAsync(context, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", "proposals"]):
                await Promotion.PromotionEndpoints.ProposeAsync(context, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", "promotions"]):
                if (await Promotion.PromotionEndpoints.PromoteAsync(context, cancel).ConfigureAwait(false) is { } promoted)
                {
                    response.StatusCode = StatusCodes.Status201Created;
                    await WriteAsync(response, View(promoted, port, canRevert: false), cancel).ConfigureAwait(false);
                }

                break;

            case ("POST", ["previews"]):
                await Promotion.PromotionEndpoints.PreviewAsync(context, port, cancel).ConfigureAwait(false);
                break;

            case ("GET", ["previews", var newToken]):
                if (context.RequestServices.GetRequiredService<AppPreviews>().Find(null, newToken) is not { } newPreview)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new PreviewReport(newPreview.Errors, newPreview.Blocked, newPreview.AskedModel), cancel).ConfigureAwait(false);
                break;

            case (_, ["previews", var receivingToken, "pages", .. var previewPagesRest]):
                if (context.RequestServices.GetRequiredService<AppPreviews>().Find(null, receivingToken) is not { Pages: { } held })
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await PagesServing.HandleControlAsync(context, held, previewPagesRest).ConfigureAwait(false);
                break;

            case ("DELETE", ["previews", var removedNew]):
                response.StatusCode = context.RequestServices.GetRequiredService<AppPreviews>().Remove(null, removedNew)
                    ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
                break;

            case ("POST", ["apps", var proposalFor, "proposals"]):
                await ProposeAsync(context, proposalFor, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var pastedFor, "proposals", "from-html"]):
                if (await catalog.GetAsync(pastedFor, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                // A change made elsewhere — an answer the person pasted back — becomes a proposal like a model's, with no model.
                var pasted = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false));
                var pastedSource = Encoding.UTF8.GetString(await catalog.ReadHtmlAsync(pastedFor, cancel).ConfigureAwait(false));
                if (Edit.PastedProposals.From(pastedSource, pasted) is not { } fromPasted)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteAsync(response, new PackageRefusal("not-a-document"), cancel).ConfigureAwait(false);
                    break;
                }

                await WriteAsync(response, new ProposalView(fromPasted.Html, fromPasted.Summary, fromPasted.Edits, Edit.PastedProposals.Model, null, null, null), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var previewOf, "previews"]):
                if (await catalog.GetAsync(previewOf, cancel).ConfigureAwait(false) is not { ArchivedAt: null })
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var previewHtml = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                if (previewHtml.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var previewToken = context.RequestServices.GetRequiredService<AppPreviews>().Create(previewOf, previewHtml);
                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, new PreviewView(previewToken, AppPreviews.Origin(previewToken, port).AbsoluteUri), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var previewedId, "previews", var readToken]):
                if (context.RequestServices.GetRequiredService<AppPreviews>().Find(previewedId, readToken) is not { } preview)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new PreviewReport(preview.Errors, preview.Blocked, preview.AskedModel), cancel).ConfigureAwait(false);
                break;

            case ("DELETE", ["apps", var previewedApp, "previews", var removedToken]):
                response.StatusCode = context.RequestServices.GetRequiredService<AppPreviews>().Remove(previewedApp, removedToken)
                    ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
                break;

            case ("POST", ["apps", var revertedId, "revisions", "revert"]):
                if (await catalog.GetAsync(revertedId, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await ChangeRevisionAsync(context, revertedId, StatusCodes.Status200OK,
                    storage => catalog.RevertAsync(revertedId, storage, cancel), app => app.Usage.RecordRevision(reverted: true)).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var importedTo, "revisions", var keptBy, "import" or "undo-import"]):
                if (await catalog.GetAsync(importedTo, cancel).ConfigureAwait(false) is null
                    || !int.TryParse(keptBy, NumberStyles.None, CultureInfo.InvariantCulture, out var keptRevision))
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                // The same lossless order as a revision change: the code stays, the data is replaced, pages reload.
                await ChangeRevisionAsync(context, importedTo, StatusCodes.Status200OK, segments[4] == "import"
                    ? storage => catalog.ImportUndoneAsync(importedTo, keptRevision, storage, cancel)
                    : storage => catalog.UndoImportAsync(importedTo, keptRevision, storage, cancel), record: null).ConfigureAwait(false);
                break;

            case ("GET" or "POST", ["apps", var importsOf, "imports", ..]):
                if (await catalog.GetAsync(importsOf, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                switch (request.Method, segments[3..])
                {
                    case ("GET", []):
                        await Adoption.TableImportEndpoints.ListAsync(context, importsOf, cancel).ConfigureAwait(false);
                        break;
                    case ("POST", ["preview"]):
                        await Adoption.TableImportEndpoints.PreviewAsync(context, importsOf, cancel).ConfigureAwait(false);
                        break;
                    case ("POST", []):
                        await Adoption.TableImportEndpoints.ImportAsync(context, importsOf, cancel).ConfigureAwait(false);
                        break;
                    case ("POST", [var undoneNumber, "undo"]) when int.TryParse(undoneNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var importNumber):
                        await Adoption.TableImportEndpoints.UndoAsync(context, importsOf, importNumber, cancel).ConfigureAwait(false);
                        break;
                    default:
                        response.StatusCode = StatusCodes.Status404NotFound;
                        break;
                }

                break;

            case ("POST", ["apps", var archiveId, "archive" or "restore"]):
                var marked = await catalog.SetArchivedAsync(archiveId, archived: segments[2] == "archive", cancel).ConfigureAwait(false);
                if (marked is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, View(marked, port, await catalog.CanRevertAsync(marked, cancel).ConfigureAwait(false), catalog.OpenUsage(marked.Id).LastUsedOn), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var keptId, "keep"]):
                var kept = await catalog.KeepAsync(keptId, cancel).ConfigureAwait(false);
                if (kept is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, View(kept, port, await catalog.CanRevertAsync(kept, cancel).ConfigureAwait(false), catalog.OpenUsage(kept.Id).LastUsedOn), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var leftId, "left"]):
                var left = await catalog.SetLeftAsync(leftId, left: true, cancel).ConfigureAwait(false);
                if (left is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, View(left, port, await catalog.CanRevertAsync(left, cancel).ConfigureAwait(false), catalog.OpenUsage(left.Id).LastUsedOn), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var exportId, "export"]):
                var target = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (!Path.IsPathFullyQualified(target))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                AdoptedApp? exported;
                try
                {
                    var open = await catalog.GetAsync(exportId, cancel).ConfigureAwait(false) is { ArchivedAt: null }
                        ? await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(exportId).ConfigureAwait(false)
                        : null;
                    var withData = request.Query["data"] != "leave";
                    exported = await WhileNotFetchingAsync(open, () => catalog.ExportAsync(exportId, target, open?.Storage, withData, cancel), cancel).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                if (exported is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new ExportedView(exported.Id, Path.GetFullPath(target)), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var packId, "package"]):
                var packageTarget = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                PackageData? packageData = request.Query["data"].ToString() switch { "none" => PackageData.None, "all" => PackageData.All, _ => null };
                if (!Path.IsPathFullyQualified(packageTarget) || !packageTarget.EndsWith(AppPackage.Extension, StringComparison.OrdinalIgnoreCase) || packageData is null)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                PackedApp? packed;
                try
                {
                    var openToPack = await catalog.GetAsync(packId, cancel).ConfigureAwait(false) is { ArchivedAt: null }
                        ? await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(packId).ConfigureAwait(false)
                        : null;
                    packed = await WhileNotFetchingAsync(openToPack,
                        () => catalog.PackAsync(packId, packageTarget, packageData.Value, AiServices, openToPack?.Storage, cancel), cancel).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                if (packed is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new PackedView(packed.App.Id, packed.Path, packed.Manifest.Version, packed.Manifest.Provenance.ContentSha256,
                    packed.Manifest.Data, packed.Manifest.Includes, packed.Manifest.Permissions), cancel).ConfigureAwait(false);
                break;

            case (_, ["apps", var sourcesId, "sources", .. var sourcesRest]):
                if (await catalog.GetAsync(sourcesId, cancel).ConfigureAwait(false) is not { ArchivedAt: null })
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await SourcesServing.HandleControlAsync(context, await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(sourcesId).ConfigureAwait(false), sourcesRest).ConfigureAwait(false);
                break;

            case (_, ["apps", var pagesId, "pages", .. var pagesRest]):
                if (await catalog.GetAsync(pagesId, cancel).ConfigureAwait(false) is not { ArchivedAt: null })
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await PagesServing.HandleControlAsync(context, (await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(pagesId).ConfigureAwait(false)).Pages, pagesRest).ConfigureAwait(false);
                break;

            case ("DELETE", ["apps", var removeId]):
                RemovedApp? removed;
                try
                {
                    if (await catalog.GetAsync(removeId, cancel).ConfigureAwait(false) is { ArchivedAt: not null } or { Unsaved: true })
                        await context.RequestServices.GetRequiredService<OpenApps>().CloseAsync(removeId).ConfigureAwait(false);
                    removed = await catalog.RemoveAsync(removeId, DiscardOf(context.RequestServices.GetRequiredService<RuntimeHostOptions>()), cancel).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                if (removed is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new RemovedView(removed.Id, removed.RemovedAt), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var usageFor, "usage"]):
                if (await catalog.GetAsync(usageFor, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                // Read from the file, like the listing: looking at the record does not open the application.
                await WriteAsync(response, UsageOf(catalog.OpenUsage(usageFor)), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var sourceOf, "source"]):
                if (await catalog.GetAsync(sourceOf, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                // The code in use, as it was taken in — for handing it to an AI the person uses elsewhere. No data comes with it.
                response.ContentType = "text/html; charset=utf-8";
                await response.Body.WriteAsync(await catalog.ReadHtmlAsync(sourceOf, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var revisionsOf, "revisions"]):
                if (await catalog.GetAsync(revisionsOf, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                // File names only — where a file was is the person's, and the list is for telling revisions apart.
                await WriteAsync(response, (await catalog.ListRevisionsAsync(revisionsOf, cancel).ConfigureAwait(false))
                    .Select(r => new RevisionView(r.Revision, r.Previous, r.TakenInAt, r.Source.OriginalPath is { Length: > 0 } p ? Path.GetFileName(p) : null, r.InUse, r.Undone switch
                    {
                        UndoneData.Importable => "importable",
                        UndoneData.Imported => "imported",
                        UndoneData.Diverged => "diverged",
                        _ => null,
                    }, r.Request, r.RestoredFrom, r.Source.Sha256))
                    .ToList(), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var tabOf, "tabs", var tabId]):
                if (context.RequestServices.GetRequiredService<LocalOrigin.AspNetCore.Storage.StorageChannel>().Sessions.Find(tabOf, tabId) is not { } tab)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new TabView(tab.LastSequence, tab.Issued, tab.Left), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var lossOf, "loss-suspected"]):
                if (await catalog.GetAsync(lossOf, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                (await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(lossOf).ConfigureAwait(false)).Usage.RecordLossSuspected();
                response.StatusCode = StatusCodes.Status204NoContent;
                break;

            case ("GET", ["apps", var id, "status"]):
                if (await catalog.GetAsync(id, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(id).ConfigureAwait(false);
                var onlineOnly = OnlineStorage.OnlyOnline(Encoding.UTF8.GetString(await catalog.ReadHtmlAsync(id, cancel).ConfigureAwait(false)));
                var today = app.Usage.Today;
                var signals = app.Usage.SignalsOn(today);
                await WriteAsync(response, new AppStatus(
                    today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    signals.Contains(UsageSignal.Opened), signals.Contains(UsageSignal.Input), signals.Contains(UsageSignal.Wrote),
                    app.Usage.LoadErrorsOn(today), app.RecentLoadErrors, app.RecentErrors, app.NeededKeys, app.Blocked, app.MissingFiles, app.MissingApis, app.Assets.Assets.Count, onlineOnly, app.NeededSpeechModel), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var assetsFor, "assets"]):
                if (await catalog.GetAsync(assetsFor, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await context.RequestServices.GetRequiredService<AssetFetcher>().FetchAsync(assetsFor, cancel).ConfigureAwait(false);
                var fetchedApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(assetsFor).ConfigureAwait(false);
                await WriteAsync(response, new AssetsView(
                    fetchedApp.Assets.Assets.Select(a => new AssetView(a.Url, a.Size)).ToList(),
                    fetchedApp.Assets.Failures.Select(f => new AssetView(f.Url, 0, f.Reason)).ToList()), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["usage-report"]):
                var reported = new List<ReportedApp>();
                foreach (var a in await catalog.ListAsync(cancel).ConfigureAwait(false))
                {
                    var usage = UsageOf(catalog.OpenUsage(a.Id));
                    reported.Add(new ReportedApp(a.Id, Iso(DateOnly.FromDateTime(a.AdoptedAt.ToLocalTime().DateTime)), a.Revision, usage,
                        a.ArchivedAt is { } archivedAt ? Iso(DateOnly.FromDateTime(archivedAt.ToLocalTime().DateTime)) : null));
                }

                foreach (var r in await catalog.ListRemovedAsync(cancel).ConfigureAwait(false))
                {
                    reported.Add(new ReportedApp(r.Id, Iso(DateOnly.FromDateTime(r.AdoptedAt.ToLocalTime().DateTime)), r.Revision, UsageOf(catalog.OpenRemovedUsage(r.Id)),
                        r.ArchivedAt is { } removedArchivedAt ? Iso(DateOnly.FromDateTime(removedArchivedAt.ToLocalTime().DateTime)) : null,
                        Iso(DateOnly.FromDateTime(r.RemovedAt.ToLocalTime().DateTime))));
                }

                await WriteAsync(response, new UsageReport(UsageReport.FormatName, DateTimeOffset.Now, reported), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["runtime"]):
                await WriteAsync(response, new RuntimeFacts(AppContract.Edition), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["web", "robots"]):
                if (!Uri.TryCreate(request.Query["url"].ToString(), UriKind.Absolute, out var siteAddress) || siteAddress.Scheme is not ("http" or "https"))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }
                await WriteAsync(response, await context.RequestServices.GetRequiredService<Agent.RobotsPolicy>().CheckAsync(siteAddress, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["egress"]):
                await WriteAsync(response, context.RequestServices.GetRequiredService<Egress>().Snapshot(), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["edit" or "agent", "model"]):
                await WriteAsync(response, EditModelViewOf(ChoiceFor(context, segments[0])), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["edit" or "agent", "model"]):
                var editModel = ChoiceFor(context, segments[0]);
                if (request.Method == "PUT")
                {
                    try
                    {
                        using var body = JsonDocument.Parse(await ReadBodyAsync(request, cancel).ConfigureAwait(false));
                        await editModel.ChooseAsync(new(body.RootElement.GetProperty("provider").GetString() ?? "", body.RootElement.GetProperty("model").GetString() ?? ""), cancel).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }
                }
                else
                {
                    await editModel.ChooseAsync(null, cancel).ConfigureAwait(false);
                }

                await WriteAsync(response, EditModelViewOf(editModel), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm"]):
                var vault = context.RequestServices.GetRequiredService<ICredentialVault>();
                await WriteAsync(response, LlmProviders.All
                    .Select(p => ProviderViewOf(p, !string.IsNullOrEmpty(vault.Read(p.VaultName)), context.RequestServices))
                    .ToList(), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm", "company-model"]):
                await WriteAsync(response, CompanyModelViewOf(context.RequestServices.GetRequiredService<CompanyModel>()), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", "company-model"]):
                var companyModel = context.RequestServices.GetRequiredService<CompanyModel>();
                CompanyModelOptions? setTo = null;
                if (request.Method == "PUT")
                {
                    try
                    {
                        using var body = JsonDocument.Parse(await ReadBodyAsync(request, cancel).ConfigureAwait(false));
                        var root = body.RootElement;
                        // A listed model is chosen by its server's name and its id, and comes with its listed limits.
                        if (root.TryGetProperty("server", out var listedServer))
                        {
                            setTo = companyModel.List?.Find(listedServer.GetString(), root.GetProperty("model").GetString());
                            if (setTo is null)
                            {
                                response.StatusCode = companyModel.List is null ? StatusCodes.Status400BadRequest : StatusCodes.Status409Conflict;
                                break;
                            }
                        }
                        // The model's limits are optional; each one left out (or null) is unknown.
                        else if (!CompanyModelOptions.TryCreate(root.GetProperty("endpoint").GetString(), root.GetProperty("model").GetString(), out setTo)
                            || !ModelLimits.TryCreate(OptionalCount(root, "contextWindow"), OptionalCount(root, "maxTokens"), OptionalFlag(root, "reasoning"), out var limits))
                        {
                            response.StatusCode = StatusCodes.Status400BadRequest;
                            break;
                        }
                        else
                        {
                            setTo = setTo! with { Limits = limits! };
                        }
                    }
                    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }
                }

                try
                {
                    await companyModel.ChooseAsync(setTo, cancel).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                await WriteAsync(response, CompanyModelViewOf(companyModel), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["llm", "company-model", "check"]):
                var checkedResult = await context.RequestServices.GetRequiredService<CompanyModel>()
                    .CheckAsync(context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(LlmProxy)), cancel).ConfigureAwait(false);
                if (checkedResult is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, checkedResult, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["llm", "company-model", "models"]):
                Uri? listFrom = null;
                string? listKey = null;
                try
                {
                    var listBody = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                    if (listBody.Length > 0)
                    {
                        using var listQuery = JsonDocument.Parse(listBody);
                        var at = listQuery.RootElement.TryGetProperty("endpoint", out var given) && given.ValueKind == JsonValueKind.String ? given.GetString() : null;
                        listKey = listQuery.RootElement.TryGetProperty("key", out var givenKey) && givenKey.ValueKind == JsonValueKind.String && givenKey.GetString() is { Length: > 0 } k ? k.Trim() : null;
                        if (!string.IsNullOrWhiteSpace(at))
                        {
                            // The same rules as setting a server; the model's name is not known yet.
                            if (!CompanyModelOptions.TryCreate(at, "-", out var asked))
                            {
                                response.StatusCode = StatusCodes.Status400BadRequest;
                                break;
                            }

                            listFrom = asked!.Endpoint;
                        }
                    }
                }
                catch (JsonException)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var serverModels = await context.RequestServices.GetRequiredService<CompanyModel>().ModelsAsync(listFrom, listKey, cancel).ConfigureAwait(false);
                if (serverModels is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, serverModels, cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", "company-model", "key"]):
                var companyKeys = context.RequestServices.GetRequiredService<ICredentialVault>();
                if (request.Method == "DELETE")
                {
                    companyKeys.Delete(context.RequestServices.GetRequiredService<CompanyModel>().KeyVaultName);
                }
                else
                {
                    var companyKey = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                    if (companyKey.Length == 0)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }

                    companyKeys.Write(context.RequestServices.GetRequiredService<CompanyModel>().KeyVaultName, companyKey);
                }

                await WriteAsync(response, CompanyModelViewOf(context.RequestServices.GetRequiredService<CompanyModel>()), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm", "local-model"]):
                await WriteAsync(response, LocalModelViewOf(context), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm", "local-model", "catalog"]):
                await WriteAsync(response, context.RequestServices.GetRequiredService<ModelDownloads>().Catalog(), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["llm", "local-model", "describe"]):
                var described = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (described.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                await WriteAsync(response, await context.RequestServices.GetRequiredService<ModelDownloads>().DescribeAsync(described, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
                break;

            case ("POST" or "DELETE", ["llm", "local-model", "download"]):
                var downloads = context.RequestServices.GetRequiredService<ModelDownloads>();
                if (request.Method == "DELETE")
                {
                    downloads.Stop();
                    await WriteAsync(response, LocalModelViewOf(context), cancel).ConfigureAwait(false);
                    break;
                }

                var wanted = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (wanted.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                ModelDownloads.Started started;
                try
                {
                    started = await downloads.StartAsync(wanted, cancel).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                if (started == ModelDownloads.Started.Busy)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                // In use at once (200) or being got in the background (202).
                response.StatusCode = started == ModelDownloads.Started.InUse ? StatusCodes.Status200OK : StatusCodes.Status202Accepted;
                await WriteAsync(response, LocalModelViewOf(context), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm", "speech-model"]):
                await WriteAsync(response, await SpeechModelViewOfAsync(context, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["llm", "speech-model", "describe"]):
                var sized = context.RequestServices.GetRequiredService<LocalSpeech>();
                if (!sized.Supported)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                await WriteAsync(response, new SpeechModelSize(await sized.DownloadSizeAsync(cancel).ConfigureAwait(false)), cancel).ConfigureAwait(false);
                break;

            case ("POST" or "DELETE", ["llm", "speech-model", "download"]):
                var speech = context.RequestServices.GetRequiredService<LocalSpeech>();
                if (request.Method == "DELETE")
                {
                    speech.StopDownload();
                }
                else
                {
                    if (!speech.Supported)
                    {
                        response.StatusCode = StatusCodes.Status409Conflict;
                        break;
                    }

                    var gotten = await speech.StartDownloadAsync(cancel).ConfigureAwait(false);
                    if (gotten == ModelDownloads.Started.Busy)
                    {
                        response.StatusCode = StatusCodes.Status409Conflict;
                        break;
                    }

                    // Here already (200) or being got in the background (202).
                    response.StatusCode = gotten == ModelDownloads.Started.InUse ? StatusCodes.Status200OK : StatusCodes.Status202Accepted;
                }

                await WriteAsync(response, await SpeechModelViewOfAsync(context, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["llm", "local-model", "load"]):
                var toLoad = context.RequestServices.GetRequiredService<LocalModel>();
                if (!toLoad.StartLoading())
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                response.StatusCode = StatusCodes.Status202Accepted;
                await WriteAsync(response, LocalModelViewOf(context), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", "local-model"]):
                var localModel = context.RequestServices.GetRequiredService<LocalModel>();
                var chosen = request.Method == "DELETE" ? null : Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                try
                {
                    await localModel.ChooseAsync(chosen, cancel).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }
                catch (ArgumentException)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                await WriteAsync(response, LocalModelViewOf(context), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", var providerId, "key"]):
                if (LlmProviders.ById(providerId) is not { } provider)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var keys = context.RequestServices.GetRequiredService<ICredentialVault>();
                if (request.Method == "DELETE")
                {
                    keys.Delete(provider.VaultName);
                }
                else
                {
                    var key = System.Text.Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                    if (key.Length == 0)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }

                    keys.Write(provider.VaultName, key);
                }

                await WriteAsync(response, ProviderViewOf(provider, request.Method != "DELETE", context.RequestServices), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["drain"]):
                await WriteAsync(response, new DrainResult(await DrainAsync(context, cancel).ConfigureAwait(false)), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["shutdown"]):
                var quiet = await DrainAsync(context, cancel).ConfigureAwait(false);
                await WriteAsync(response, new DrainResult(quiet), cancel).ConfigureAwait(false);
                await response.CompleteAsync().ConfigureAwait(false);
                context.RequestServices.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                break;

            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    /// <summary>
    /// Replaces the code an application runs, in an order that loses nothing: writes already on
    /// their way — typically the last one a page sends as it closes — are waited for and applied
    /// first; then pages loaded before can no longer write; only then is the data saved and the
    /// code switched. The application's code from other hosts is fetched again afterwards, since
    /// the new revision may load different code.
    /// </summary>
    private static async Task ChangeRevisionAsync(HttpContext context, string appId, int successStatus,
        Func<KeyValueStore, Task<AdoptedApp>> change, Action<OpenApp>? record)
    {
        var services = context.RequestServices;
        var response = context.Response;
        var cancel = context.RequestAborted;
        // Not a new revision (the same bytes), or nothing to go back to: a 409, already written.
        if (await ChangeDataAsync(context, appId, change, record).ConfigureAwait(false) is not { } changed) return;

        if (services.GetRequiredService<RuntimeHostOptions>().FetchAssetsOnAdoption)
            services.GetRequiredService<AssetFetcher>().Start(appId);
        var catalog = services.GetRequiredService<AdoptionCatalog>();
        response.StatusCode = successStatus;
        var canRevert = await catalog.CanRevertAsync(changed, cancel).ConfigureAwait(false);
        await WriteAsync(response, View(changed, context.Request.Host.Port ?? 80, canRevert), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes an application's data the one way that loses nothing: one change at a time; writes
    /// already on their way are waited for and applied first; then pages loaded before can no longer
    /// write (they reload); only then does <paramref name="change"/> run. Answers what it returned, or
    /// <see langword="null"/> — with a 409 written — when it refused with <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static async Task<T?> ChangeDataAsync<T>(HttpContext context, string appId, Func<KeyValueStore, Task<T>> change, Action<OpenApp>? record = null)
        where T : class
    {
        var services = context.RequestServices;
        var cancel = context.RequestAborted;
        var app = await services.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        await app.RevisionChange.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            // Drain before revoking: a write the closing page sent must land, not be refused as stale.
            await DrainAsync(context, cancel).ConfigureAwait(false);
            services.GetRequiredService<LocalOrigin.AspNetCore.Storage.StorageChannel>().Sessions.Revoke(appId);
            T changed;
            try
            {
                changed = await change(app.Storage).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return null;
            }

            app.ForgetObservations();
            record?.Invoke(app);
            return changed;
        }
        finally
        {
            app.RevisionChange.Release();
        }
    }

    private static Task<bool> DrainAsync(HttpContext context, CancellationToken cancellationToken) =>
        context.RequestServices.GetRequiredService<Activity>().WaitForQuietAsync(Quiet, DrainLimit, cancellationToken);

    private static bool Authorized(HttpRequest request, string secret)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[scheme.Length..]), Encoding.UTF8.GetBytes(secret));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string? OriginalPath(HttpRequest request) => Escaped(request, OriginalPathHeader);

    /// <summary>A header carrying text that may not be ASCII, percent-encoded by the sender; <see langword="null"/> when absent.</summary>
    private static string? Escaped(HttpRequest request, string header) =>
        request.Headers[header].ToString() is { Length: > 0 } encoded ? Uri.UnescapeDataString(encoded) : null;

    private static async Task<MatchView> ViewMatchAsync(AdoptionCatalog catalog, AdoptionMatch match, int port, CancellationToken cancellationToken) =>
        new(View(match.App, port, await catalog.CanRevertAsync(match.App, cancellationToken).ConfigureAwait(false)), match.Kind switch
        {
            AdoptionMatchKind.SameBytes => "sameBytes",
            AdoptionMatchKind.SameOriginalPath => "sameOriginalPath",
            AdoptionMatchKind.SameName => "sameName",
            AdoptionMatchKind.SameStoredKeys => "sameStoredKeys",
            _ => throw new ArgumentOutOfRangeException(nameof(match)),
        });

    /// <summary>
    /// The applications to list, and the ones that could not be read. An application whose revisions
    /// or usage record cannot be opened moves to the unreadable ones rather than failing the whole list.
    /// </summary>
    private static async Task<(List<AppView> Apps, List<UnreadableApp> Unreadable)> ListAsync(AdoptionCatalog catalog, int port, CancellationToken cancel)
    {
        var listing = await catalog.ReadListingAsync(cancel).ConfigureAwait(false);
        var apps = new List<AppView>();
        var unreadable = new List<UnreadableApp>(listing.Unreadable);
        foreach (var a in listing.Apps)
        {
            try
            {
                apps.Add(View(a, port, await catalog.CanRevertAsync(a, cancel).ConfigureAwait(false), catalog.OpenUsage(a.Id).LastUsedOn));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new UnreadableApp(a.Id, UnreadableApp.CannotOpen, exception.Message));
            }
        }

        return (apps, unreadable);
    }

    /// <summary>
    /// Runs <paramref name="copy"/> — a copy of the application's folder — while no background fetch of its code is
    /// writing there: a fetch runs after every adoption and new revision, and rewrites the folder's index through a
    /// temporary file a copy listing the folder at that moment would fail on.
    /// </summary>
    private static async Task<T> WhileNotFetchingAsync<T>(OpenApp? app, Func<Task<T>> copy, CancellationToken cancel)
    {
        if (app is null) return await copy().ConfigureAwait(false);
        await app.AssetFetch.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return await copy().ConfigureAwait(false);
        }
        finally
        {
            app.AssetFetch.Release();
        }
    }

    private static AppView View(AdoptedApp app, int port, bool canRevert, DateOnly? lastUsed = null) =>
        new(app.Id, RuntimeHost.AppOrigin(app.Id, port).ToString(), app.AdoptedAt, app.Source.Sha256, app.Source.OriginalPath, app.Source.Size,
            app.Revision, app.RevisedAt, canRevert, lastUsed?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), app.ArchivedAt,
            app.Unsaved, app.LeftAt, app.LeftAt + AdoptionCatalog.UnsavedRetention, app.Title, app.ForkedFrom, app.InstalledFrom);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static UsageView UsageOf(UsageLog log)
    {
        var used = log.UsedDays;
        var retention = Retention.Of(used, log.Today);
        return new UsageView(
            Iso(log.Today),
            used is [var first, ..] ? Iso(first) : null,
            used is [.., var last] ? Iso(last) : null,
            retention.Day,
            System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(retention.State.ToString()),
            log.Days.Select(d => new UsageDayView(Iso(d.Date), d.Opened, d.Input, d.Wrote, d.LoadErrors, d.LossSuspected, d.Repaired)).ToList(),
            log.Revisions.Select(r => new RevisionEventView(Iso(r.Date), r.Reverted ? "reverted" : "revised")).ToList(),
            log.KeyReports.Select(k => new KeyReportView(k.Revision, Iso(k.Date), k.Missing, k.Unread, k.Seeded)).ToList());
    }

    private static Task WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken) =>
        response.WriteAsJsonAsync(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ControlJson.Default.GetTypeInfo(typeof(T))!, cancellationToken: cancellationToken);

    /// <summary>
    /// An adopted application. <c>Sha256</c>, <c>OriginalPath</c> and <c>Size</c> describe the revision in use;
    /// <c>CanRevert</c> says whether there is a previous revision to go back to. <c>LastUsed</c>
    /// (local <c>yyyy-MM-dd</c>) is filled in the listing only. <c>Unsaved</c> marks a result not kept yet;
    /// <c>LeftAt</c> is when the person last left it and <c>ExpiresAt</c> when the next start after it removes it.
    /// <c>Title</c> names an application when no file does — one the runtime made, or one whose change was applied without a file; otherwise an adopted file is known by <c>OriginalPath</c>.
    /// <c>ForkedFrom</c> names the application in the package an application was taken in separately from (<c>{ id, name, version }</c>).
    /// <c>InstalledFrom</c> is the registry it was installed from (<c>{ registry, channel, version }</c>).
    /// </summary>
    internal sealed record AppView(string Id, string Origin, DateTimeOffset AdoptedAt, string Sha256, string? OriginalPath, long Size,
        int Revision, DateTimeOffset? RevisedAt, bool CanRevert, string? LastUsed = null, DateTimeOffset? ArchivedAt = null,
        bool Unsaved = false, DateTimeOffset? LeftAt = null, DateTimeOffset? ExpiresAt = null, string? Title = null, AppFork? ForkedFrom = null,
        AppInstall? InstalledFrom = null);

    /// <summary>An earlier adoption and how it matches: <c>"sameBytes"</c>, <c>"sameOriginalPath"</c>, <c>"sameName"</c> (same folder, same name but for a browser's download number) or <c>"sameStoredKeys"</c> (the source names every key the application stored).</summary>
    internal sealed record MatchView(AppView App, string Match);

    /// <param name="Contract">The edition of the application contract this runtime serves.</param>
    internal sealed record RuntimeFacts(int Contract);

    /// <summary>
    /// Today's facts about one application. Structured only — turning them into sentences for a
    /// person is the caller's job, in the person's language. <c>OnlineOnlyStorage</c> names the online
    /// database the application keeps its data in when it has no local storage of its own — what it
    /// writes there is not kept (<see cref="OnlineStorage"/>). <c>NeedsSpeechModel</c>: it sent a recording to be
    /// turned into text while no model here could — getting the speech model on this computer would answer it.
    /// </summary>
    internal sealed record AppStatus(string Date, bool Opened, bool Input, bool Wrote, int LoadErrors, IReadOnlyList<string> RecentLoadErrors, IReadOnlyList<string> RecentErrors,
        IReadOnlyList<string> NeedsKey, IReadOnlyList<BlockedResource> Blocked, IReadOnlyList<string> MissingFiles, IReadOnlyList<MissingApi> MissingApis, int CachedAssets,
        string? OnlineOnlyStorage, bool NeedsSpeechModel);

    /// <summary>
    /// An application's usage record. <c>FirstUsed</c> is day 0 of the retention rule (<c>null</c> until
    /// the first day of use); <c>Day</c> counts from it to <c>Today</c>. <c>Retention</c> is one of
    /// <c>not-started</c>, <c>too-early</c>, <c>in-window</c>, <c>retained</c>, <c>lapsed</c>. <c>Keys</c> is the latest
    /// report per revision of how its pages read the stored data — a revision that asks for keys the data
    /// lacks while leaving stored keys unread may have changed the data's shape.
    /// </summary>
    internal sealed record UsageView(string Today, string? FirstUsed, string? LastUsed, int? Day, string Retention,
        IReadOnlyList<UsageDayView> Days, IReadOnlyList<RevisionEventView> Revisions, IReadOnlyList<KeyReportView> Keys);

    internal sealed record KeyReportView(int Revision, string Date, int Missing, int Unread, int Seeded);

    /// <summary>
    /// Every application's usage record in one document (<c>bohm.usage-report/0</c>). It identifies
    /// applications only by their random ids and carries no names, original paths or data — only when
    /// they were added, which revision runs, and the usage record. <c>GeneratedAt</c> carries the
    /// local offset so days can be read as the person's days.
    /// </summary>
    internal sealed record UsageReport(string Format, DateTimeOffset GeneratedAt, IReadOnlyList<ReportedApp> Apps)
    {
        public const string FormatName = "bohm.usage-report/0";
    }

    /// <param name="ArchivedOn">The day the person put the application away, if they did — a day, like
    /// <c>AdoptedOn</c>, never a time. Someone judging the report reads it next to a lapse: an application
    /// put away may simply have served its purpose.</param>
    /// <param name="RemovedOn">The day the person removed the application for good, if they did. Its usage record is kept for this report.</param>
    internal sealed record ReportedApp(string Id, string AdoptedOn, int Revision, UsageView Usage, string? ArchivedOn = null, string? RemovedOn = null);

    internal sealed record RemovedView(string Id, DateTimeOffset RemovedAt);

    /// <summary>
    /// One revision in an application's history: <c>file</c> is the name of the file it came from, <see langword="null"/> for an applied change.
    /// <c>undone</c>, for a revision the application was put back from, is where the data it wrote stands: <c>"importable"</c>,
    /// <c>"imported"</c> or <c>"diverged"</c> (see <see cref="UndoneData"/>).
    /// </summary>
    internal sealed record RevisionView(int Revision, int? Previous, DateTimeOffset TakenInAt, string? File, bool InUse, string? Undone, string? Request, int? RestoredFrom,
        string Sha256);

    internal sealed record ExportedView(string Id, string Path);

    internal sealed record PackageRefusal(string Reason);

    private static PackageRefusal Refusal(InvalidPackageException e) => new(e.Problem switch
    {
        PackageProblem.NotAPackage => "not-a-package",
        PackageProblem.UnknownFormat => "unknown-format",
        PackageProblem.TooLarge => "too-large",
        _ => "damaged",
    });

    internal sealed record AlreadyHereView(string Id, bool SameCode, bool SameCodeInUse);

    internal sealed record PackedView(string Id, string Path, string Version, string ContentSha256, string Data, IReadOnlyList<string> Includes, PackagePermissions Permissions);

    /// <summary>Each AI service an application may call, with the text that names it in a page — its host, or the path its calls to the organization's model server arrive under.</summary>
    private static readonly IReadOnlyDictionary<string, string> AiServices = LlmProviders.All.ToDictionary(p => p.Id, p => p.Host)
        .Append(KeyValuePair.Create(LlmProxy.CompanyModelSegment, "/__bohm/llm/" + LlmProxy.CompanyModelSegment + "/")).ToDictionary();

    internal static Func<string, CancellationToken, Task> DiscardOf(RuntimeHostOptions options) =>
        options.Discard ?? ((folder, _) =>
        {
            if (Bohm.Runtime.Files.RecycleBin.Available)
            {
                Bohm.Runtime.Files.RecycleBin.Send(folder);
            }
            else
            {
                var discarded = Path.Combine(options.DataRoot, "discarded");
                Directory.CreateDirectory(discarded);
                Directory.Move(folder, Path.Combine(discarded, DiscardedName(folder)));
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// The name a removed application's folder is kept under where it is discarded — its own name and a mark of its own:
    /// the same application can be removed again (taken back in from a package or a registry, then removed), and a
    /// second removal must not find the first one in its way.
    /// </summary>
    internal static string DiscardedName(string folder) => Path.GetFileName(folder) + "-" + Guid.NewGuid().ToString("n")[..8];

    internal sealed record UsageDayView(string Date, bool Opened, bool Input, bool Wrote, int LoadErrors, int LossSuspected, int Repaired);

    internal sealed record RevisionEventView(string Date, string Event);

    internal sealed record AssetView(string Url, long Size, string? Reason = null);

    internal sealed record AssetsView(IReadOnlyList<AssetView> Cached, IReadOnlyList<AssetView> NotCached);

    private static LocalModelView LocalModelViewOf(HttpContext context)
    {
        var local = context.RequestServices.GetRequiredService<LocalModel>();
        return new(local.Current?.ModelPath, local.Loaded, local.Fixed, local.Loading, local.LastFailure,
            context.RequestServices.GetRequiredService<ModelDownloads>().Current);
    }

    private static async Task<SpeechModelView> SpeechModelViewOfAsync(HttpContext context, CancellationToken cancel)
    {
        var speech = context.RequestServices.GetRequiredService<LocalSpeech>();
        return new(speech.Supported, await speech.DownloadedAsync(cancel).ConfigureAwait(false), speech.Loaded, speech.LastFailure, speech.Download);
    }

    /// <param name="Supported">Whether this copy can run a speech model at all.</param>
    /// <param name="Downloaded">Whether the model is on this computer — recordings are then turned into text without the network.</param>
    /// <param name="Failure">The model library's message from the last load that failed, shown as is, or <see langword="null"/>.</param>
    /// <param name="Download">The model being got, or the last attempt that ended without it; <see langword="null"/> when neither.</param>
    internal sealed record SpeechModelView(bool Supported, bool Downloaded, bool Loaded, string? Failure, ModelDownloads.DownloadView? Download);

    /// <param name="SizeBytes">What getting the model takes, as its host lists it, or <see langword="null"/> when the host could not be asked.</param>
    internal sealed record SpeechModelSize(long? SizeBytes);

    /// <param name="ModelPath">The model file, or <see langword="null"/> when none is chosen.</param>
    /// <param name="Failure">Why the last load failed — a reason and its values, no sentence — or <see langword="null"/>.</param>
    /// <param name="Download">The model being got onto this computer, or the last attempt that ended without it; <see langword="null"/> when neither.</param>
    internal sealed record LocalModelView(string? ModelPath, bool Loaded, bool Fixed, bool Loading, LocalModelFailure? Failure, ModelDownloads.DownloadView? Download);

    /// <summary>
    /// A proposal for the application's current source, from the model on this computer. Nothing is
    /// kept: the proposal is the answer, and taking it in is the caller's next request.
    /// </summary>
    private static async Task AgentTurnAsync(HttpContext context, CancellationToken cancel)
    {
        var response = context.Response;
        List<Microsoft.Extensions.AI.ChatMessage> conversation;
        bool last;
        try
        {
            using var body = JsonDocument.Parse(await ReadBodyAsync(context.Request, cancel).ConfigureAwait(false));
            conversation = Agent.WebAgent.ParseConversation(body.RootElement);
            // The caller's last round for this question: answer from what was read, with no more tool calls.
            last = body.RootElement.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var agentModel = context.RequestServices.GetRequiredService<Edit.AgentModel>();
        Edit.ChosenEditModel? model;
        try
        {
            model = await agentModel.GetAsync(cancel).ConfigureAwait(false);
        }
        catch (LocalModelUnavailableException e)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new ProposalFailure(e.Failure, null, null), cancel).ConfigureAwait(false);
            return;
        }

        if (model is not { } chosen)
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            await WriteAsync(response, agentModel.Missing ?? new Edit.EditModelMissing("localModel", null), cancel).ConfigureAwait(false);
            return;
        }

        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetLocalNow();
        if (!AcceptsLines(context.Request))
        {
            Agent.TurnResult turn;
            try
            {
                turn = await Agent.WebAgent.RunTurnAsync(chosen.Client, chosen.Name, chosen.OnThisComputer, chosen.Limits, conversation, now, null, cancel, last).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancel.IsCancellationRequested)
            {
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await WriteAsync(response, new ProposalFailure(null, e.Message, Edit.ProviderRefusal.Of(e)), cancel).ConfigureAwait(false);
                return;
            }

            await WriteAsync(response, turn, cancel).ConfigureAwait(false);
            return;
        }

        // One JSON object per line, sent as the model writes: { text } for each piece of its text, then the turn as the
        // plain answer has it — or, since the status went out before the model was asked, { status: "failed", … } with what
        // a 503 would carry.
        response.ContentType = NdJson;
        await response.StartAsync(cancel).ConfigureAwait(false);
        try
        {
            var turn = await Agent.WebAgent.RunTurnAsync(chosen.Client, chosen.Name, chosen.OnThisComputer, chosen.Limits, conversation, now,
                (text, token) => WriteLineAsync(response, new TurnText(text), token), cancel, last).ConfigureAwait(false);
            await WriteLineAsync(response, turn, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            await WriteLineAsync(response, new TurnFailure("failed", null, e.Message, Edit.ProviderRefusal.Of(e)), cancel).ConfigureAwait(false);
        }
    }

    internal const string NdJson = "application/x-ndjson";

    private static readonly byte[] NewLine = [(byte)'\n'];

    internal static bool AcceptsLines(HttpRequest request) =>
        request.GetTypedHeaders().Accept.Any(accept => string.Equals(accept.MediaType.Value, NdJson, StringComparison.OrdinalIgnoreCase));

    private static Task WriteLineAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken) =>
        WriteLineAsync(response, value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ControlJson.Default.GetTypeInfo(typeof(T))!, cancellationToken);

    /// <summary>One line of an answer sent as it is made (<see cref="NdJson"/>): the value, a line end, flushed.</summary>
    internal static async Task WriteLineAsync<T>(HttpResponse response, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(value, type);
        await response.Body.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await response.Body.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A piece of the answer's text, as the model wrote it.</summary>
    internal sealed record TurnText(string Text);

    /// <summary>How many of a failed proposal's problems the model is told — the first ones; the rest usually follow from them.</summary>
    private const int MaxProblemsTold = 10;

    /// <summary>A streamed turn the model could not finish — the fields of <see cref="ProposalFailure"/>, with the status a turn line has.</summary>
    internal sealed record TurnFailure(string Status, LocalModelFailure? Model, string? Detail, Edit.ProviderRefusal? Provider);

    private static async Task ProposeAsync(HttpContext context, string appId, CancellationToken cancel)
    {
        var response = context.Response;
        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        if (await catalog.GetAsync(appId, cancel).ConfigureAwait(false) is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Either «change this» on an element — an instruction and a target — or a named fix the runtime
        // words itself: `{"fix": "local-storage"}` moves an application that keeps its data only online.
        string instruction = "";
        Edit.EditTarget? target = null;
        List<string>? problems = null;
        var source = Encoding.UTF8.GetString(await catalog.ReadHtmlAsync(appId, cancel).ConfigureAwait(false));
        try
        {
            using var body = JsonDocument.Parse(await ReadBodyAsync(context.Request, cancel).ConfigureAwait(false));
            var root = body.RootElement;
            if (root.TryGetProperty("fix", out var fix))
            {
                // Only an application that has the problem: the proposal would otherwise rewrite working storage.
                if (fix.GetString() != "local-storage" || OnlineStorage.OnlyOnline(source) is null)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
            else
            {
                instruction = root.GetProperty("instruction").GetString() ?? "";
                var element = root.GetProperty("target");
                target = new Edit.EditTarget(element.GetProperty("html").GetString() ?? "",
                    element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                // The earlier proposal for this request failed when the shell opened it with a copy of the data:
                // fixed from that version, with what went wrong told, rather than made again from the saved one.
                if (root.TryGetProperty("broken", out var broken))
                {
                    var html = broken.GetProperty("html").GetString();
                    problems = broken.GetProperty("problems").EnumerateArray().Select(p => p.GetString()?.Trim() ?? "").Where(p => p.Length > 0)
                        .Take(MaxProblemsTold).Select(p => p.Length > 500 ? p[..500] : p).ToList();
                    if (string.IsNullOrWhiteSpace(html) || problems.Count == 0)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }

                    source = html;
                }
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var editModel = context.RequestServices.GetRequiredService<Edit.EditModel>();
        Edit.ChosenEditModel? model;
        try
        {
            model = await editModel.GetAsync(cancel).ConfigureAwait(false);
        }
        catch (LocalModelUnavailableException e)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new ProposalFailure(e.Failure, null, null), cancel).ConfigureAwait(false);
            return;
        }

        if (model is null)
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            await WriteAsync(response, editModel.Missing ?? new Edit.EditModelMissing("localModel", null), cancel).ConfigureAwait(false);
            return;
        }

        // Asked for lines: as for a new application (`apps/proposals`) — { thinking } as a model that thinks thinks, { writing, start } for each replacement's new text as the
        // model writes it, then { status: "done", … } with the proposal or { status: "failed", … } with what a 503 would carry.
        var lines = AcceptsLines(context.Request);
        Func<Edit.ProposalProgress, CancellationToken, Task>? onProgress = null;
        if (lines)
        {
            response.ContentType = NdJson;
            await response.StartAsync(cancel).ConfigureAwait(false);
            onProgress = (progress, token) => WriteLineAsync(response, progress, Promotion.PromotionJson.Default.ProposalProgress, token);
        }

        Edit.EditProposal proposal;
        try
        {
            proposal = await (target is null
                ? Edit.EditProposals.ProposeLocalStorageAsync(model.Client, model.OnThisComputer, model.Limits, source, cancel)
                : Edit.EditProposals.ProposeAsync(model.Client, model.OnThisComputer, model.Limits, source, target, instruction, problems, cancel, onProgress)).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            // The model stopped without finishing — the local server's request limit, or a provider's refusal,
            // which carries the provider's own status and message so the person can see why.
            var (detail, provider, stopped) = (e.Message, Edit.ProviderRefusal.Of(e), (e as Edit.ProposalFailedException)?.Stopped);
            if (lines)
            {
                await WriteLineAsync(response, new Promotion.PromotionEndpoints.ProposalFailedLine("failed", null, detail, provider, stopped), Promotion.PromotionJson.Default.ProposalFailedLine, cancel).ConfigureAwait(false);
                return;
            }

            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new ProposalFailure(null, detail, provider, stopped), cancel).ConfigureAwait(false);
            return;
        }

        if (lines)
            await WriteLineAsync(response, new ProposalLine("done", proposal.Html, proposal.Summary, proposal.Edits, model.Name, proposal.Complete, proposal.Left, proposal.Stopped), cancel).ConfigureAwait(false);
        else
            await WriteAsync(response, new ProposalView(proposal.Html, proposal.Summary, proposal.Edits, model.Name, proposal.Complete, proposal.Left, proposal.Stopped), cancel).ConfigureAwait(false);
    }

    /// <param name="Html">The whole source with the edits made — what to take in as a new revision.</param>
    /// <param name="Summary">The model's sentence on what it changed.</param>
    /// <param name="Edits">Each exact piece replaced and its replacement, in order; empty when nothing changed.</param>
    /// <param name="Model">Which model proposed it: <c>local</c>, or the provider and model name (<c>openai/…</c>).</param>
    /// <param name="Complete">For a named fix, whether it finished — a proposal that stopped halfway is not one to apply. <c>null</c> for a change the person asked for.</param>
    /// <param name="Remaining">For a named fix that did not finish, what it left.</param>
    /// <param name="Stopped"><c>output-limit</c> when the model's answer reached its length limit after making these edits, <c>step-limit</c> when it used all its rounds — they may not be all it meant to make; otherwise <see langword="null"/>.</param>
    internal sealed record ProposalView(string Html, string Summary, IReadOnlyList<Edit.SourceEdit> Edits, string Model, bool? Complete, Edit.StorageLeft? Remaining, string? Stopped);

    /// <summary>The last line of a change proposed as it goes: <see cref="ProposalView"/>'s fields, with the status a last line has (<c>done</c>).</summary>
    internal sealed record ProposalLine(string Status, string Html, string Summary, IReadOnlyList<Edit.SourceEdit> Edits, string Model, bool? Complete, Edit.StorageLeft? Remaining, string? Stopped);

    /// <param name="Provider">The chosen provider's id, or <see langword="null"/> for the model on this computer.</param>
    /// <param name="Model">The chosen model's name, or <see langword="null"/>.</param>
    /// <param name="Missing">What is missing before a proposal can be made, or <see langword="null"/>.</param>
    internal sealed record EditModelView(string? Provider, string? Model, Edit.EditModelMissing? Missing);

    private static EditModelView EditModelViewOf(Edit.ProviderChoice model) => new(model.Chosen?.Provider, model.Chosen?.Model, model.Missing);

    /// <summary>The model choice behind <c>/__control/edit/model</c> (proposals) or <c>/__control/agent/model</c> (questions about web pages).</summary>
    private static Edit.ProviderChoice ChoiceFor(HttpContext context, string which) => which == "agent"
        ? context.RequestServices.GetRequiredService<Edit.AgentModel>()
        : context.RequestServices.GetRequiredService<Edit.EditModel>();

    /// <param name="Model">Why the model could not start, when that is why.</param>
    /// <param name="Detail">What stopped the model, when it started and did not finish.</param>
    /// <param name="Provider">The provider's refusal — its status and own message — when a provider refused.</param>
    /// <param name="Stopped">What ended the model's answer before it finished, when that is known: <c>output-limit</c> (its length limit) or <c>step-limit</c> (its rounds of tool calls).</param>
    internal sealed record ProposalFailure(LocalModelFailure? Model, string? Detail, Edit.ProviderRefusal? Provider, string? Stopped = null);

    private static ProviderView ProviderViewOf(LlmProvider provider, bool connected, IServiceProvider services) =>
        new(provider.Id, provider.DisplayName, provider.Host, connected,
            connected || !ChatBridges.AnswersChat(provider) ? null
            : services.GetRequiredService<CompanyModel>().Configured ? "company"
            : services.GetRequiredService<LocalModel>().Configured ? "local"
            : null);

    /// <param name="AnsweredBy">What, with no key connected, answers this provider's chat requests: <c>company</c> (the organization's model server), <c>local</c> (the model on this computer) or <see langword="null"/> (nothing — the application is told to connect a key).</param>
    internal sealed record ProviderView(string Id, string Name, string Host, bool Connected, string? AnsweredBy);

    private static CompanyModelView CompanyModelViewOf(CompanyModel company) =>
        new(company.Current?.Endpoint.AbsoluteUri, company.Current?.Model, company.Fixed, company.KeyConnected,
            company.Current?.Limits.ContextWindow, company.Current?.Limits.MaxOutputTokens, company.Current?.Limits.Reasoning,
            company.ReportedContextWindow, NullIfEmpty(company.Current?.Server), company.Current?.DisplayName,
            company.List?.Choices.Select(c => new CompanyModelChoiceView(NullIfEmpty(c.Server), c.Model, c.DisplayName, c.Endpoint.AbsoluteUri,
                c.Limits.ContextWindow, c.Limits.MaxOutputTokens, c.Limits.Reasoning, c.Input ?? ["text"], company.KeyConnectedFor(c.Server))).ToList());

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <param name="Endpoint">The server's OpenAI-compatible base address, or <see langword="null"/> when none is set.</param>
    /// <param name="ContextWindow">The model's context window as it was set, or <see langword="null"/> when unknown.</param>
    /// <param name="MaxTokens">The most tokens one answer may have, as it was set, or <see langword="null"/> when unknown.</param>
    /// <param name="Reasoning">Whether the model thinks before it answers, as it was set, or <see langword="null"/> when unknown.</param>
    /// <param name="ReportedContextWindow">The context window the server reported for the model, once it was asked; used when <paramref name="ContextWindow"/> is not set.</param>
    /// <param name="Fixed">Whether an administrator listed the servers, so the person only chooses among <paramref name="Choices"/>.</param>
    /// <param name="Server">The listed server in use, by its name; <see langword="null"/> for one set by its address or given the older way.</param>
    /// <param name="Name">The name to show for the model in use, when the list gives one.</param>
    /// <param name="Choices">Every listed model, when the servers are listed; <see langword="null"/> otherwise.</param>
    internal sealed record CompanyModelView(string? Endpoint, string? Model, bool Fixed, bool KeyConnected, int? ContextWindow, int? MaxTokens, bool? Reasoning,
        int? ReportedContextWindow, string? Server, string? Name, IReadOnlyList<CompanyModelChoiceView>? Choices);

    /// <summary>One listed model the person may choose.</summary>
    /// <param name="KeyConnected">Whether a key is connected for its server.</param>
    internal sealed record CompanyModelChoiceView(string? Server, string Model, string? Name, string Endpoint, int? ContextWindow, int? MaxTokens, bool? Reasoning,
        IReadOnlyList<string> Input, bool KeyConnected);

    /// <summary>
    /// A whole number at <paramref name="name"/>, or <see langword="null"/> when it is missing or null.
    /// </summary>
    /// <exception cref="InvalidOperationException">It is there but is not a whole number that fits.</exception>
    private static int? OptionalCount(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number
        : throw new InvalidOperationException($"{name} is not a whole number.");

    /// <summary>A true or false at <paramref name="name"/>, or <see langword="null"/> when it is missing or null.</summary>
    /// <exception cref="InvalidOperationException">It is there but is neither true nor false.</exception>
    private static bool? OptionalFlag(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
        : throw new InvalidOperationException($"{name} is neither true nor false.");

    internal sealed record DrainResult(bool Quiet);

    internal sealed record TabView(long Ack, long Issued, bool Left);

    /// <param name="ShareTarget">For a proposed new application whose manifest says it receives pages, the query names it gives them — what the person tries it with through <c>POST /__control/previews/{token}/pages</c>.</param>
    internal sealed record PreviewView(string Token, string Origin, Bohm.Runtime.Pages.ShareTarget? ShareTarget = null);

    internal sealed record PreviewReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Blocked, bool AskedModel);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.AppView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.MatchView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<UnreadableApp>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.RuntimeFacts))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.UsageView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.UsageReport))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.DrainResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.TabView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(EgressSnapshot))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Agent.SiteVerdict))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BlockedResource))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MissingApi))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProposalView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProposalLine))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProposalFailure))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.EditModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Edit.EditModelMissing))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AssetsView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProviderView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.LocalModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.SpeechModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.SpeechModelSize))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.CompanyModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.CompanyModelChoiceView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Agent.TurnResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.TurnText))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.TurnFailure))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.RemovedView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.RevisionView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ExportedView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.PackedView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.PackageRefusal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PackageInspection))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.RegistryReadView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RegistryPublished))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AlreadyHereView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.PreviewView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.PreviewReport))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CompanyModel.CheckResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CompanyModel.ModelsResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(IReadOnlyList<ModelDownloads.CatalogView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ModelDownloads.Description))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.ProviderView>))]
internal sealed partial class ControlJson : System.Text.Json.Serialization.JsonSerializerContext;
