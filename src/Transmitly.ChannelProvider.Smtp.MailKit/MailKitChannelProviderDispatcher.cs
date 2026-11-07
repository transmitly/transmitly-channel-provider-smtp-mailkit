// Copyright (c) Code Impressions, LLC. All Rights Reserved.
//  
//  Licensed under the Apache License, Version 2.0 (the "License")
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  
//      http://www.apache.org/licenses/LICENSE-2.0
//  
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Transmitly.Util;
using Config = Transmitly.ChannelProvider.Smtp.Configuration;

namespace Transmitly.ChannelProvider.Smtp.MailKit
{
	public sealed class MailKitChannelProviderClient(Func<ISmtpClient> createClient, Config.SmtpOptions optionObj) : ChannelProviderDispatcher<IEmail>
	{
		private const int DefaultSmtpPort = 587;
		private const int DefaultSslSmtpPort = 465;

		public MailKitChannelProviderClient(Config.SmtpOptions optionObj) : this(() => new SmtpClientWrapper(), optionObj)
		{

		}

		private readonly Config.SmtpOptions _optionObj = Guard.AgainstNull(optionObj);
		private readonly Func<ISmtpClient> _createClient = Guard.AgainstNull(createClient);

		public override async Task<IReadOnlyCollection<IDispatchResult?>> DispatchAsync(IEmail email, IDispatchCommunicationContext communicationContext, CancellationToken cancellationToken)
		{
			Guard.AgainstNull(email);
			Guard.AgainstNull(communicationContext);

			var msg = new MimeMessage
			{
				MessageId = MimeUtils.GenerateMessageId()
			};
			msg.From.Add(email.From.ToMailboxAddress());
			msg.To.AddRange(email.To!.Select(m => m.ToMailboxAddress()));
			msg.Subject = email.Subject!;

			if (email.Bcc != null)
				msg.Bcc.AddRange(email.Bcc.Select(x => x.ToMailboxAddress()));

			if (email.Cc != null)
				msg.Cc.AddRange(email.Cc.Select(x => x.ToMailboxAddress()));

			if (email.ReplyTo != null)
				msg.ReplyTo.AddRange(email.ReplyTo.Select(x => x.ToMailboxAddress()));

			var body = new BodyBuilder()
			{
				HtmlBody = email.HtmlBody,
				TextBody = email.TextBody
			};

			AddAttachments(body, email.Attachments, communicationContext.ContentModel?.LinkedResources, cancellationToken);

			msg.Body = body.ToMessageBody();

			using var client = _createClient();
			await Connect(client, cancellationToken).ConfigureAwait(false);
			string result = await Send(msg, client, cancellationToken).ConfigureAwait(false);
			var commResult = new MailKitSendResult
			{
				ResourceId = msg.MessageId,
				MessageString = result,
				Status = CommunicationsStatus.Success(nameof(MailKitChannelProviderClient), "Dispatched")
			};
			SendDeliveryReports(email, communicationContext, commResult);
			return [commResult];
		}

		private void SendDeliveryReports(IEmail email, IDispatchCommunicationContext communicationContext, MailKitSendResult commResult)
		{
			if (commResult.Status.IsFailure())
				Error(communicationContext, email, [commResult]);
			else
				Dispatched(communicationContext, email, [commResult]);
		}

		private static async Task<string> Send(MimeMessage msg, ISmtpClient client, CancellationToken cancellationToken)
		{
			//If SmtpClient.Send() throws an exception, then sending the message failed. If it doesn't throw an exception, then it succeeded.
			//https://github.com/jstedfast/MailKit/issues/861#issuecomment-496497579
			var result = await client.SendAsync(msg, cancellationToken).ConfigureAwait(false);
			await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
			return result;
		}

		private async Task Connect(ISmtpClient client, CancellationToken cancellationToken)
		{
			await client.ConnectAsync(_optionObj.Host, ResolvePort(_optionObj), Convert(_optionObj.SocketOptions), cancellationToken).ConfigureAwait(false);
			var encoding = ResolveEncoding(_optionObj);
			if (_optionObj.Credentials != null)
			{
				await client.AuthenticateAsync(encoding, _optionObj.Credentials, cancellationToken).ConfigureAwait(false);
				return;
			}

			// No credentials: internal relays without authentication
			if (string.IsNullOrEmpty(_optionObj.UserName))
				return;

			await client.AuthenticateAsync(encoding, _optionObj.UserName, _optionObj.Password, cancellationToken).ConfigureAwait(false);
		}

		private static int ResolvePort(Config.SmtpOptions options)
		{
			if (options.Port.HasValue)
				return options.Port.Value;

			return options.SocketOptions == Config.SecureSocketOptions.SslOnConnect
				? DefaultSslSmtpPort
				: DefaultSmtpPort;
		}

		private static Encoding ResolveEncoding(Config.SmtpOptions options) => options.Encoding ?? Encoding.UTF8;

		private static SecureSocketOptions Convert(Config.SecureSocketOptions socketOptions)
		{
			return socketOptions switch
			{
				Config.SecureSocketOptions.None => SecureSocketOptions.None,
				Config.SecureSocketOptions.Auto => SecureSocketOptions.Auto,
				Config.SecureSocketOptions.SslOnConnect => SecureSocketOptions.SslOnConnect,
				Config.SecureSocketOptions.StartTls => SecureSocketOptions.StartTls,
				Config.SecureSocketOptions.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
				_ => throw new NotSupportedException("Unsupported socket option: " + socketOptions)
			};
		}

		private static void AddAttachments(BodyBuilder body, IReadOnlyCollection<IEmailAttachment> attachments, IReadOnlyList<LinkedResource>? linkedResources, CancellationToken cancellationToken)
		{
			foreach (var attachment in attachments)
			{
				var (mediaType, subType) = GetContentType(attachment.ContentType);
				var name = string.IsNullOrWhiteSpace(attachment.Name) ? Guid.NewGuid().ToString("N") : attachment.Name!;
				body.Attachments.Add(name, Rewind(attachment.ContentStream), new ContentType(mediaType, subType), cancellationToken);
			}

			foreach (var resource in linkedResources ?? [])
			{
				// HTML refers to an inline image as cid:<content id>; accept ids given with or without that prefix.
				var contentId = resource.Id.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ? resource.Id.Substring(4) : resource.Id;
				var (mediaType, subType) = GetContentType(resource.ContentType);
				var part = body.LinkedResources.Add(resource.Name ?? contentId, Rewind(resource.ContentStream), new ContentType(mediaType, subType), cancellationToken);
				part.ContentId = contentId;
			}
		}

		// Every recipient's email shares the same resource streams, so read each one from the start.
		private static Stream Rewind(Stream? stream)
		{
			Guard.AgainstNull(stream);
			if (stream!.CanSeek)
				stream.Position = 0;
			return stream;
		}

		private static (string mediaType, string subType) GetContentType(string? contentType)
		{
			(string, string) unknownContentType = ("application", "octet-stream");
			if (string.IsNullOrWhiteSpace(contentType))
				return unknownContentType;
			var separateTypes = contentType!.Split('/');
			if (separateTypes.Length != 2)
				return unknownContentType;
			return (separateTypes[0], separateTypes[1]);
		}
	}
}