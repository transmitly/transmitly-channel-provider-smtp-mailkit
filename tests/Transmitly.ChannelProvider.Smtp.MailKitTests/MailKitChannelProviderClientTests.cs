// ﻿﻿Copyright (c) Code Impressions, LLC. All Rights Reserved.
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

using MimeKit;
using Moq;
using System.Text;
using Transmitly.ChannelProvider.Smtp.Configuration;
using Transmitly.ChannelProvider.Smtp.MailKit;
using Transmitly.Delivery;
using MKS = MailKit.Security;
namespace Transmitly.ChannelProvider.Smtp.Tests
{
	[TestClass]
	public class MailKitChannelProviderClientTests
	{
		private Mock<ISmtpClient> _smtpClientMock;
		private SmtpOptions _smtpOptions;

		[TestInitialize]
		public void Setup()
		{
			_smtpClientMock = new Mock<ISmtpClient>();

			// Setup a valid SmtpOptions instance for testing.
			_smtpOptions = new SmtpOptions
			{
				Host = "smtp.test.com",
				Port = 587,
				SocketOptions = Configuration.SecureSocketOptions.None,
				UserName = "user@test.com",
				Password = "password"
			};
		}

		[TestMethod]
		[ExpectedException(typeof(ArgumentNullException))]
		public async Task DispatchAsync_NullEmail_ThrowsArgumentNullException()
		{
			// Arrange
			var client = new MailKitChannelProviderClient(_smtpClientMock.Object, _smtpOptions);
			IEmail? email = null; // Intentionally null
			var context = new Mock<IDispatchCommunicationContext>().Object;

			// Act
			await client.DispatchAsync(email, context, CancellationToken.None);
		}

		[TestMethod]
		public async Task DispatchAsync_NullCommunicationContext_ThrowsArgumentNullException()
		{
			var client = new MailKitChannelProviderClient(_smtpClientMock.Object, _smtpOptions);
			var email = CreateValidTestEmail();
			IDispatchCommunicationContext? context = null;

			await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => client.DispatchAsync(email, context, CancellationToken.None));
		}

		[TestMethod]
		public async Task DispatchAsync_ValidEmail_ReturnsDispatchedResult()
		{
			var email = CreateValidTestEmail();
			var contextMock = new Mock<IDispatchCommunicationContext>();
			contextMock.Setup(c => c.DeliveryReportManager).Returns(new Mock<IDeliveryReportService>().Object);
			_smtpClientMock.Setup(c => c.ConnectAsync(_smtpOptions.Host, _smtpOptions.Port.Value,
				It.IsAny<MKS.SecureSocketOptions>(), It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask)
				.Verifiable();

			_smtpClientMock.Setup(c => c.AuthenticateAsync(_smtpOptions.UserName, _smtpOptions.Password,
				It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask)
				.Verifiable();

			string sendResultString = "SentMessage";
			MimeMessage? capturedMessage = null;
			_smtpClientMock.Setup(c => c.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
				.Callback<MimeMessage, CancellationToken>((msg, token) => capturedMessage = msg)
				.ReturnsAsync(sendResultString)
				.Verifiable();

			_smtpClientMock.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask)
				.Verifiable();

			var client = new MailKitChannelProviderClient(_smtpClientMock.Object, _smtpOptions);

			var results = await client.DispatchAsync(email, contextMock.Object, CancellationToken.None);

			Assert.IsNotNull(results, "Result should not be null.");
			Assert.AreEqual(1, results.Count, "Result count should be one.");
			var dispatchResult = results.First();
			Assert.IsNotNull(dispatchResult, "Dispatch result should not be null.");
			Assert.IsTrue(dispatchResult.Status.IsSuccess(), "Expected status to be Successful.");
			Assert.AreEqual("Dispatched", dispatchResult.Status.Type, "Expected status to be Dispatched.");
			Assert.IsFalse(string.IsNullOrWhiteSpace(dispatchResult.ResourceId), "ResourceId should be set.");

			// Verify the MimeMessage was built correctly.
			Assert.AreEqual(email.Subject, capturedMessage.Subject, "Subject does not match.");
			// Check that the sender was added (assuming extension ToMailboxAddress converts correctly).
			Assert.IsTrue(capturedMessage.From.Mailboxes.Any(m => m.Address == email.From.Value),
				"Sender email was not correctly converted.");

			_smtpClientMock.Verify();
		}

		[TestMethod]
		public async Task DispatchAsync_EmailWithAttachments_AttachmentsAreAdded()
		{
			var email = CreateValidTestEmail(withAttachments: true);
			var contextMock = new Mock<IDispatchCommunicationContext>();
			contextMock.Setup(c => c.DeliveryReportManager).Returns(new Mock<IDeliveryReportService>().Object);
			var port = _smtpOptions.Port ?? 587; 

			_smtpClientMock.Setup(c => c.ConnectAsync(_smtpOptions.Host, port,
				It.IsAny<MKS.SecureSocketOptions>(), It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask);

			_smtpClientMock.Setup(c => c.AuthenticateAsync(_smtpOptions.UserName, _smtpOptions.Password,
				It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask);

			string sendResultString = "SentMessage";
			MimeMessage? capturedMessage = null;
			_smtpClientMock.Setup(c => c.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
				.Callback<MimeMessage, CancellationToken>((msg, token) => capturedMessage = msg)
				.ReturnsAsync(sendResultString);

			_smtpClientMock.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask);

			var client = new MailKitChannelProviderClient(_smtpClientMock.Object, _smtpOptions);

			var results = await client.DispatchAsync(email, contextMock.Object, CancellationToken.None);

			// Assert: Verify attachments were processed.
			Assert.IsNotNull(capturedMessage);
			Assert.IsTrue(capturedMessage.Body is Multipart, "Expected a multipart MIME body.");
			var multipart = (Multipart)capturedMessage.Body;
			bool hasAttachment = multipart.OfType<MimePart>().Any();
			Assert.IsTrue(hasAttachment, "Expected an attachment part in the MIME message.");

			_smtpClientMock.Verify(c => c.ConnectAsync(_smtpOptions.Host, port,
				It.IsAny<MKS.SecureSocketOptions>(), It.IsAny<CancellationToken>()), Times.Once());
			_smtpClientMock.Verify(c => c.AuthenticateAsync(_smtpOptions.UserName, _smtpOptions.Password,
				It.IsAny<CancellationToken>()), Times.Once());
			_smtpClientMock.Verify(c => c.SendAsync(It.IsAny<MimeMessage>(),
				It.IsAny<CancellationToken>()), Times.Once());
			_smtpClientMock.Verify(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()), Times.Once());
		}

		[TestMethod]
		public async Task DispatchAsync_EmailWithInvalidAttachmentContentType_UsesDefaultContentType()
		{
			var email = CreateValidTestEmail(withAttachments: true, invalidAttachmentContentType: true);
			var contextMock = new Mock<IDispatchCommunicationContext>();
			contextMock.Setup(c => c.DeliveryReportManager).Returns(new Mock<IDeliveryReportService>().Object);
			_smtpClientMock.Setup(c => c.ConnectAsync(_smtpOptions.Host, _smtpOptions.Port.Value,
				It.IsAny<MKS.SecureSocketOptions>(), It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask);

			_smtpClientMock.Setup(c => c.AuthenticateAsync(_smtpOptions.UserName, _smtpOptions.Password,
				It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask);

			string sendResultString = "SentMessage";
			MimeMessage? capturedMessage = null;
			_smtpClientMock.Setup(c => c.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
				.Callback<MimeMessage, CancellationToken>((msg, token) => capturedMessage = msg)
				.ReturnsAsync(sendResultString);

			_smtpClientMock.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()))
				.Returns(Task.CompletedTask);

			var client = new MailKitChannelProviderClient(_smtpClientMock.Object, _smtpOptions);

			var results = await client.DispatchAsync(email, contextMock.Object, CancellationToken.None);

			Assert.IsNotNull(capturedMessage);
			var multipart = capturedMessage.Body as Multipart;
			Assert.IsNotNull(multipart, "Expected a multipart MIME body.");
			var attachments = multipart.Where(part => part is MimePart).Cast<MimePart>().ToList();
			Assert.IsTrue(attachments.Any(), "Expected at least one attachment.");

			foreach (var attachment in attachments)
			{
				Assert.AreEqual("application", attachment.ContentType.MediaType,
					"Invalid media type was not defaulted.");
				Assert.AreEqual("octet-stream", attachment.ContentType.MediaSubtype,
					"Invalid media subtype was not defaulted.");
			}
		}


		private IEmail CreateValidTestEmail(bool withAttachments = false, bool invalidAttachmentContentType = false)
		{
			var email = new TestEmail
			{
				From = "sender@test.com".AsIdentityAddress("Sender"),
				To = ["recipient@test.com".AsIdentityAddress("Recipient")],
				// Optionally set Bcc, Cc, and ReplyTo if needed.
				Subject = "Test Subject",
				HtmlBody = "<p>Hello</p>",
				TextBody = "Hello"
			};

			if (withAttachments)
			{
				var attachment = new TestEmailAttachment
				{
					Name = "attachment.txt",
					ContentType = invalidAttachmentContentType ? "invalid" : "text/plain",
					ContentStream = new MemoryStream(Encoding.UTF8.GetBytes("Test content"))
				};

				email.Attachments = [attachment];
			}
			else
			{
				email.Attachments = Array.Empty<IEmailAttachment>();
			}

			return email;
		}
	}
}