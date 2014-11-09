using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Elasticsearch.Net.Connection.RequestState;
using Elasticsearch.Net.ConnectionPool;
using Elasticsearch.Net.Exceptions;
using Elasticsearch.Net.Providers;
using Elasticsearch.Net.Serialization;

namespace Elasticsearch.Net.Connection.RequestHandlers
{
	public class RequestHandlerBase
	{
		protected const int BUFFER_SIZE = 4096;
		protected static readonly string MaxRetryExceptionMessage = "Failed after retrying {2} times: '{0} {1}'. {3}";
		protected static readonly string MaxRetryInnerMessage = "InnerException: {0}, InnerMessage: {1}, InnerStackTrace: {2}";

		protected readonly IConnectionConfigurationValues _settings;
		protected readonly IConnection _connection;
		protected readonly IConnectionPool _connectionPool;
		protected readonly IElasticsearchSerializer _serializer;
		protected readonly IMemoryStreamProvider _memoryStreamProvider;
		protected readonly ITransportDelegator _delegator;

		protected readonly bool _throwMaxRetry;

		protected RequestHandlerBase(IConnectionConfigurationValues settings,
			IConnection connection,
			IConnectionPool connectionPool,
			IElasticsearchSerializer serializer,
			IMemoryStreamProvider memoryStreamProvider,
			ITransportDelegator delegator)
		{
			_settings = settings;
			_connection = connection;
			_connectionPool = connectionPool;
			_serializer = serializer;
			_memoryStreamProvider = memoryStreamProvider;
			_delegator = delegator;

			this._throwMaxRetry = !(this._connectionPool is SingleNodeConnectionPool);
		}

		protected byte[] PostData(object data)
		{
			var bytes = data as byte[];
			if (bytes != null)
				return bytes;

			var s = data as string;
			if (s != null)
				return s.Utf8Bytes();
			if (data == null) return null;
			var ss = data as IEnumerable<string>;
			if (ss != null)
				return (string.Join("\n", ss) + "\n").Utf8Bytes();

			var so = data as IEnumerable<object>;
			if (so == null)
				return this._serializer.Serialize(data);
			var joined = string.Join("\n", so
				.Select(soo => this._serializer.Serialize(soo, SerializationFormatting.None).Utf8String())) + "\n";
			return joined.Utf8Bytes();
		}

		protected static bool IsValidResponse(ITransportRequestState requestState, IElasticsearchResponse streamResponse)
		{
			return streamResponse.Success ||
			       (!streamResponse.Success
			        && requestState.RequestConfiguration != null
			        && requestState.RequestConfiguration.AllowedStatusCodes.HasAny(i => i == streamResponse.HttpStatusCode)
				       );

		}

		protected bool TypeOfResponseCopiesDirectly<T>()
		{
			var type = typeof(T);
			return type == typeof(string) || type == typeof(byte[]) || typeof(Stream).IsAssignableFrom(typeof(T));
		}


		protected bool ReturnStringOrByteArray<T>(ElasticsearchResponse<T> original, byte[] bytes, out ElasticsearchResponse<T> response)
		{
			response = original;
			var type = typeof(T);
			if (type == typeof(string))
			{
				this.SetStringResult(original as ElasticsearchResponse<string>, bytes);
				return true;
			}
			if (type == typeof(byte[]))
			{
				this.SetByteResult(original as ElasticsearchResponse<byte[]>, bytes);
				return true;
			}
			return false;
		}

		protected void ThrowMaxRetryExceptionWhenNeeded<T>(TransportRequestState<T> requestState, int maxRetries)
		{
			if (requestState.Retried < maxRetries) return;
			var innerExceptions = requestState.SeenExceptions.Where(e => e != null).ToList();
			var innerException = !innerExceptions.HasAny()
				? null
				: (innerExceptions.Count() == 1)
					? innerExceptions.First()
					: new AggregateException(requestState.SeenExceptions);
			var exceptionMessage = CreateMaxRetryExceptionMessage(requestState, innerException);
			throw new MaxRetryException(exceptionMessage, innerException);
		}

		protected string CreateMaxRetryExceptionMessage<T>(TransportRequestState<T> requestState, Exception e)
		{
			string innerException = null;
			if (e != null)
			{
				var aggregate = e as AggregateException;
				if (aggregate != null)
				{

					aggregate = aggregate.Flatten();
					var innerExceptions = aggregate.InnerExceptions
						.Select(ae => MaxRetryInnerMessage.F(ae.GetType().Name, ae.Message, ae.StackTrace))
						.ToList();
					innerException = "\r\n" + string.Join("\r\n", innerExceptions);
				}
				else
					innerException = "\r\n" + MaxRetryInnerMessage.F(e.GetType().Name, e.Message, e.StackTrace);
			}
			var exceptionMessage = MaxRetryExceptionMessage
				.F(requestState.Method, requestState.Path, requestState.Retried, innerException);
			return exceptionMessage;
		}

		protected void SetErrorDiagnosticsAndPatchSuccess<T>(
			ITransportRequestState requestState,
			ElasticsearchServerError error,
			ElasticsearchResponse<T> typedResponse,
			IElasticsearchResponse streamResponse)
		{
			if (error != null)
			{
				typedResponse.Success = false;
				typedResponse.OriginalException = new ElasticsearchServerException(error);
			}
			if (!typedResponse.Success
			    && requestState.RequestConfiguration != null
			    && requestState.RequestConfiguration.AllowedStatusCodes.HasAny(i => i == streamResponse.HttpStatusCode))
			{
				typedResponse.Success = true;
			}
		}

		protected void SetStringResult(ElasticsearchResponse<string> response, byte[] rawResponse)
		{
			response.Response = rawResponse.Utf8String();
		}

		protected void SetByteResult(ElasticsearchResponse<byte[]> response, byte[] rawResponse)
		{
			response.Response = rawResponse;
		}

		protected ElasticsearchServerError ThrowOrGetErrorFromStreamResponse<T>(
			TransportRequestState<T> requestState,
			ElasticsearchResponse<Stream> streamResponse)
		{
			if (((streamResponse.HttpStatusCode.HasValue && streamResponse.HttpStatusCode.Value <= 0)
			     || !streamResponse.HttpStatusCode.HasValue) && streamResponse.OriginalException != null)
				throw streamResponse.OriginalException;

			if (IsValidResponse(requestState, streamResponse))
				return null;

			ElasticsearchServerError error = null;

			//If type is stream or voidresponse do not attempt to read error from stream this is handled by the user
			var type = typeof(T);
			if (typeof(Stream).IsAssignableFrom(type) || type == typeof(VoidResponse))
				return null;

			var readFrom = streamResponse.Response;
			var hasResponse = readFrom != null && readFrom.Length > 0;
			if (hasResponse && (this._settings.KeepRawResponse || this.TypeOfResponseCopiesDirectly<T>()))
			{
				var ms = this._memoryStreamProvider.New();
				readFrom.CopyTo(ms);
				streamResponse.ResponseRaw = this._settings.KeepRawResponse ? ms.ToArray() : null;
				streamResponse.Response.Close();
				readFrom = ms;
				readFrom.Position = 0;
			}
			if (hasResponse)
			{
				try
				{
					var e = this._serializer.Deserialize<OneToOneServerException>(readFrom);
					error = ElasticsearchServerError.Create(e);
				}
					// ReSharper disable once EmptyGeneralCatchClause
					// parsing failure of exception should not be fatal, its a best case helper.
				catch { }

				//streamResponse.Response.Close();
				readFrom.Position = 0;
				streamResponse.Response = readFrom;

			}
			else
				error = new ElasticsearchServerError
				{
					Status = streamResponse.HttpStatusCode.GetValueOrDefault(-1)
				};


			if (this._settings.ThrowOnElasticsearchServerExceptions)
				throw new ElasticsearchServerException(error);
			return error;
		}
	}
}