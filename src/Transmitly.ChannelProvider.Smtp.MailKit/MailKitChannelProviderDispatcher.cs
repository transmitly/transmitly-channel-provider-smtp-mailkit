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
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Transmitly.Util;
using Config = Transmitly.ChannelProvider.Smtp.Configuration;

namespace Transmitly.ChannelProvider.Smtp.MailKit
{
	public sealed class MailKitChannelProviderClient(ISmtpClient client, Config.SmtpOptions optionObj) : ChannelProviderDispatcher<IEmail>
	{
		private const int DefaultSmtpPort = 587;
		private const int DefaultSslSmtpPort = 465;

		public MailKitChannelProviderClient(Config.SmtpOptions optionObj) : this(new SmtpClientWrapper(), optionObj)
		{

		}

		private readonly Config.SmtpOptions _optionObj = Guard.AgainstNull(optionObj);
		private readonly ISmtpClient _client = client;

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
			msg.Subject = email.Subject;

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

			AddAttachments(body, email.Attachments, cancellationToken);

			msg.Body = body.ToMessageBody();

			await Connect(_client, cancellationToken).ConfigureAwait(false);
			string result = await Send(msg, _client, cancellationToken).ConfigureAwait(false);
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

		private static void AddAttachments(BodyBuilder body, IReadOnlyCollection<IEmailAttachment> attachments, CancellationToken cancellationToken)
		{
			foreach (var attachment in attachments)
			{
				var (mediaType, subType) = GetContentType(attachment.ContentType);
				body.Attachments.Add(attachment.Name, attachment.ContentStream, new ContentType(mediaType, subType), cancellationToken);
			}
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