﻿using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using static WebOne.Program;

namespace WebOne
{
	/// <summary>
	/// Fake HTTPS server, which simulates a remote HTTPS server.
	/// </summary>
	class HttpSecureServer
	{
		Stream ClientStreamReal;
		SslStream ClientStreamTunnel;
		X509Certificate2 Certificate;
		HttpRequest RequestReal;
		HttpResponse ResponseReal;
		LogWriter Logger;

		/// <summary>
		/// Start fake HTTPS server emulation for already established NetworkStream.
		/// </summary>
		public HttpSecureServer(HttpRequest Request, HttpResponse Response, LogWriter Logger)
		{
			// Get outer HTTP/1.1 part of tunnel
			RequestReal = Request;
			ResponseReal = Response;
			ClientStreamReal = Request.InputStream;
			this.Logger = Logger;
#if DEBUG
			Logger.WriteLine(">SSL: {0}", Request.RawUrl);
#endif

			//Certificate = RootCertificate; //temporary - WebOne CA certificate

			// Make a fake certificate for current domain, signed by CA certificate
			string HostName = RequestReal.RawUrl.Substring(0, RequestReal.RawUrl.IndexOf(":"));
			Certificate = CertificateUtil.MakeChainSignedCert("CN=" + HostName, RootCertificate);
		}

		/// <summary>
		/// Accept an incoming "connection" by establishing SSL tunnel &amp; start data exchange.
		/// </summary>
		public void Accept()
		{
			// Answer that this proxy supports HTTPS
			ResponseReal.ProtocolVersionString = "HTTP/1.1";
			ResponseReal.StatusCode = 200; //better be "HTTP/1.0 200 Connection established", but "HTTP/1.1 200 OK" is OK too
			ResponseReal.AddHeader("Via", "HTTPS/1.0 WebOne/" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
			ResponseReal.SendHeaders();

			try
			{
				// Perform SSL handshake and establish a inner tunnel
				ClientStreamTunnel = new SslStream(ClientStreamReal, false);
				//ClientStream.AuthenticateAsServer(Certificate, false, SslProtocols.Tls | SslProtocols.Ssl3 | SslProtocols.Ssl2, true);
				//ClientStreamTunnel.AuthenticateAsServer(Certificate, false, SslProtocols.Default, false);
				ClientStreamTunnel.AuthenticateAsServer(Certificate, false, SslProtocols.Ssl2 | SslProtocols.Ssl3 | SslProtocols.Tls | SslProtocols.Tls12, false);
				//ClientStreamTunnel.AuthenticateAsServer(Certificate, false, SslProtocols.Ssl3, false);

				/* Result:
				 * Ssl2 with Rc4 128-bit, Md5 128-bit
				 * Ssl3 with TripleDes 168-bit, Sha1 160-bit
				 * Tls with Aes256 256-bit, Sha1 160-bit
				 * Tls12 with Aes256 256-bit, Sha1 160-bit
				 */
			}
			catch (Exception HandshakeEx)
			{
				string err = HandshakeEx.Message;
				if (HandshakeEx.InnerException != null) err = HandshakeEx.InnerException.Message;
				Logger.WriteLine("!SSL Handshake failed: {0} ({1})", err, HandshakeEx.HResult);
				ClientStreamReal.Close();
				return;
			}

			// Work with unencrypted HTTP inside tunnel
			try
			{
				LogWriter Logger = new();
				HttpUtil.SslClient sslc = new();
				sslc.Stream = ClientStreamTunnel;
				sslc.LocalEndPoint = RequestReal.LocalEndPoint;
				sslc.RemoteEndPoint = RequestReal.RemoteEndPoint;
				sslc.TargetServer = RequestReal.RawUrl;
				sslc.Encrypting = string.Format("{0} with {1} {2}-bit, {3} {4}-bit",
				ClientStreamTunnel.SslProtocol,
				ClientStreamTunnel.CipherAlgorithm.ToString(),
				ClientStreamTunnel.CipherStrength,
				ClientStreamTunnel.HashAlgorithm.ToString(),
				ClientStreamTunnel.HashStrength);
				HttpUtil.ProcessClientRequest(sslc, Logger);
			}
			catch (IOException)
			{
				// Unexpected close (hello, Kaspersky AV traffic scan)
				if (!ConfigFile.HideClientErrors) Logger.WriteLine(" SSL Client disconnected.");
				ClientStreamTunnel.Close();
			}
			catch (Exception ex)
			{
				Logger.WriteLine("SslOops: {0}.", ex.Message);
				try { ClientStreamTunnel.Close(); } catch { }
			}
			Logger.WriteLine("<Close SSL.");
		}
	}
}
