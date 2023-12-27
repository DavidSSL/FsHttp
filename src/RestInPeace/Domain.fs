[<AutoOpen>]
module RestInPeace.Domain

open System.Threading

type StatusCodeExpectation = {
    expected: System.Net.HttpStatusCode list
    actual: System.Net.HttpStatusCode
}

exception StatusCodeExpectedxception of StatusCodeExpectation

type BodyPrintMode = {
    format: bool
    maxLength: int option
}

type PrintMode<'bodyPrintMode> =
    | AsObject
    | HeadersOnly
    | HeadersAndBody of BodyPrintMode

type PrintHint = {
    requestPrintMode: PrintMode<unit>
    responsePrintMode: PrintMode<BodyPrintMode>
}

type CertErrorStrategy =
    | Default
    | AlwaysAccept

type Proxy = {
    url: string
    credentials: System.Net.ICredentials option
}

type HttpClientHandlerTransformer = (System.Net.Http.SocketsHttpHandler -> System.Net.Http.SocketsHttpHandler)

and Config = {
    timeout: System.TimeSpan option
    defaultDecompressionMethods: System.Net.DecompressionMethods list
    printHint: PrintHint
    httpMessageTransformers: list<System.Net.Http.HttpRequestMessage -> System.Net.Http.HttpRequestMessage>
    httpClientHandlerTransformers: list<HttpClientHandlerTransformer>
    httpClientTransformers: list<System.Net.Http.HttpClient -> System.Net.Http.HttpClient>
    httpCompletionOption: System.Net.Http.HttpCompletionOption
    proxy: Proxy option
    certErrorStrategy: CertErrorStrategy
    httpClientFactory: Config -> System.Net.Http.HttpClient
    // Calls `LoadIntoBufferAsync` of the response's HttpContent immediately after receiving.
    bufferResponseContent: bool
    cancellationToken: CancellationToken
}

type ConfigTransformer = Config -> Config

type PrintHintTransformer = PrintHint -> PrintHint

type RestInPeaceUrl = {
    address: string option
    additionalQueryParams: List<string * string>
}

type Header = {
    method: System.Net.Http.HttpMethod option
    headers: Map<string, string>
    // We use a .Net type here, which we never do in other places.
    // Since Cookie is record style, I see no problem here.
    cookies: System.Net.Cookie list
}

type ContentData =
    | TextContent of string
    | BinaryContent of byte array
    | StreamContent of System.IO.Stream
    | FormUrlEncodedContent of Map<string, string>
    | FileContent of string

type ContentType = {
    value: string
    charset: System.Text.Encoding option
}

type ContentElement = {
    contentData: ContentData
    explicitContentType: ContentType option
}

type RequestContent =
    | Empty
    | Single of BodyContent
    | Multi of MultipartContent

and BodyContent = {
    contentElement: ContentElement
    headers: Map<string, string>
}

and MultipartContent = {
    partElements: MultipartElement list
    headers: Map<string, string>
}

and MultipartElement = {
    name: string
    content: ContentElement
    fileName: string option
}

type Request = {
    url: RestInPeaceUrl
    header: Header
    content: RequestContent
    config: Config
}

type IToRequest =
    abstract member Transform: unit -> Request

type IConfigure<'t, 'self> =
    abstract member Configure: 't -> 'self

// It seems to impossible extending builder methods on the context type
// directly when they are not polymorph.
type IRequestContext<'self> =
    abstract member Self: 'self

let configPrinter (c: IConfigure<ConfigTransformer, _>) transformPrintHint =
    c.Configure(fun conf -> { conf with printHint = transformPrintHint conf.printHint })

// Unifying IToBodyContext and IToMultipartContext doesn't work; see:
// https://github.com/dotnet/fsharp/issues/12814
type IToBodyContext =
    inherit IToRequest
    abstract member Transform: unit -> BodyContext

and IToMultipartContext =
    inherit IToRequest
    abstract member Transform: unit -> MultipartContext

// TODO: Convert this to a class.
and HeaderContext = {
    url: RestInPeaceUrl
    header: Header
    config: Config
} with
    interface IRequestContext<HeaderContext> with
        member this.Self = this

    interface IConfigure<ConfigTransformer, HeaderContext> with
        member this.Configure(transformConfig) = { this with config = transformConfig this.config }

    interface IConfigure<PrintHintTransformer, HeaderContext> with
        member this.Configure(transformPrintHint) = configPrinter this transformPrintHint

    interface IToRequest with
        member this.Transform() = {
            url = this.url
            header = this.header
            content = Empty
            config = this.config
        }

    interface IToBodyContext with
        member this.Transform() = {
            url = this.url
            header = this.header
            bodyContent = {
                contentElement = {
                    contentData = BinaryContent [||]
                    explicitContentType = None
                }
                headers = Map.empty
            }
            config = this.config
        }

    interface IToMultipartContext with
        member this.Transform() = {
            url = this.url
            header = this.header
            config = this.config
            multipartContent = {
                partElements = []
                headers = Map.empty
            }
        }

// TODO: Convert this to a class.
and BodyContext = {
    url: RestInPeaceUrl
    header: Header
    bodyContent: BodyContent
    config: Config
} with
    interface IRequestContext<BodyContext> with
        member this.Self = this

    interface IConfigure<ConfigTransformer, BodyContext> with
        member this.Configure(transformConfig) = { this with config = transformConfig this.config }

    interface IConfigure<PrintHintTransformer, BodyContext> with
        member this.Configure(transformPrintHint) = configPrinter this transformPrintHint

    interface IToRequest with
        member this.Transform() = {
            url = this.url
            header = this.header
            content = Single this.bodyContent
            config = this.config
        }

    interface IToBodyContext with
        member this.Transform() = this

// TODO: Convert this to a class.
and MultipartContext = {
    url: RestInPeaceUrl
    header: Header
    multipartContent: MultipartContent
    config: Config
} with
    interface IRequestContext<MultipartContext> with
        member this.Self = this

    interface IConfigure<ConfigTransformer, MultipartContext> with
        member this.Configure(transformConfig) = { this with config = transformConfig this.config }

    interface IConfigure<PrintHintTransformer, MultipartContext> with
        member this.Configure(transformPrintHint) = configPrinter this transformPrintHint

    interface IToRequest with
        member this.Transform() = {
            url = this.url
            header = this.header
            content = Multi this.multipartContent
            config = this.config
        }

    interface IToMultipartContext with
        member this.Transform() = this

// TODO: Convert this to a class.
and MultipartElementContext = {
    parent: MultipartContext
    part: MultipartElement
} with

    interface IRequestContext<MultipartElementContext> with
        member this.Self = this

    interface IConfigure<ConfigTransformer, MultipartElementContext> with
        member this.Configure(transformConfig) =
            let updatedCfg = this.parent.config |> transformConfig
            { this with parent.config = updatedCfg }

    interface IConfigure<PrintHintTransformer, MultipartElementContext> with
        member this.Configure(transformPrintHint) = configPrinter this transformPrintHint

    interface IToRequest with
        member this.Transform() =
            let parentWithSelf = (this :> IToMultipartContext).Transform()
            (parentWithSelf :> IToRequest).Transform()

    interface IToMultipartContext with
        member this.Transform() =
            let parentElementsAndSelf = this.parent.multipartContent.partElements @ [ this.part ]
            { this with parent.multipartContent.partElements = parentElementsAndSelf }.parent

type Response = {
    request: Request
    requestMessage: System.Net.Http.HttpRequestMessage
    content: System.Net.Http.HttpContent
    headers: System.Net.Http.Headers.HttpResponseHeaders
    reasonPhrase: string
    statusCode: System.Net.HttpStatusCode
    version: System.Version
    originalHttpRequestMessage: System.Net.Http.HttpRequestMessage
    originalHttpResponseMessage: System.Net.Http.HttpResponseMessage
    dispose: unit -> unit
} with
    interface IConfigure<PrintHintTransformer, Response> with
        member this.Configure(transformPrintHint) = 
            { this with request.config.printHint = transformPrintHint this.request.config.printHint }

    interface System.IDisposable with
        member this.Dispose() = this.dispose ()
