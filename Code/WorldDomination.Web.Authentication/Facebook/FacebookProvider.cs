﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Web.Mvc;
using CuttingEdge.Conditions;
using RestSharp;
using RestSharp.Contrib;

namespace WorldDomination.Web.Authentication.Facebook
{
    // REFERENCE: http://developers.facebook.com/docs/authentication/server-side/

    public class FacebookProvider : IAuthenticationProvider
    {
        private const string ScopeKey = "&scope={0}";
        private const string StateKey = "&state={0}";
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly Uri _redirectUri;
        private readonly IRestClient _restClient;
        private readonly IList<string> _scope;

        public FacebookProvider(string clientId, string clientSecret, Uri redirectUri)
            : this(clientId, clientSecret, redirectUri, null, null)
        {
        }

        public FacebookProvider(string clientId, string clientSecret, Uri redirectUri, IRestClient restClient)
            : this(clientId, clientSecret, redirectUri, null, restClient)
        {
        }

        public FacebookProvider(string clientId, string clientSecret, Uri redirectUri, IList<string> scope)
            : this(clientId, clientSecret, redirectUri, scope, null)
        {
        }

        public FacebookProvider(string clientId, string clientSecret, Uri redirectUri, IList<string> scope,
                                IRestClient restClient)
        {
            Condition.Requires(clientId).IsNotNullOrEmpty();
            Condition.Requires(clientSecret).IsNotNullOrEmpty();
            Condition.Requires(redirectUri).IsNotNull();

            _clientId = clientId;
            _clientSecret = clientSecret;
            _redirectUri = redirectUri;

            // Optionals.
            _scope = scope;
            _restClient = restClient ?? new RestClient("http://graph.facebook.com");
        }

        private string RetrieveAccessToken(string code)
        {
            Condition.Requires(code).IsNotNullOrEmpty();

            IRestResponse response;
            try
            {
                //    "/oauth/access_token?client_id={0}&redirect_uri={1}&client_secret={2}&code={3}";
                var restRequest = new RestRequest("oauth/access_token");
                restRequest.AddParameter("client_id", _clientId);
                restRequest.AddParameter("redirect_uri", _redirectUri);
                restRequest.AddParameter("client_secret", _clientSecret);
                restRequest.AddParameter("code", code);

                response = _restClient.Execute(restRequest);
            }
            catch (Exception exception)
            {
                throw new AuthenticationException("Failed to retrieve an oauth access token from Facebook.", exception);
            }

            if (response == null ||
                response.StatusCode != HttpStatusCode.OK)
            {
                throw new AuthenticationException(
                    string.Format(
                        "Failed to obtain an Access Token from Facebook OR the the response was not an HTTP Status 200 OK. Response Status: {0}. Response Description: {1}",
                        response == null ? "-- null response --" : response.StatusCode.ToString(),
                        response == null ? string.Empty : response.StatusDescription));
            }

            var querystringParameters = HttpUtility.ParseQueryString(response.Content);
            var accessToken = querystringParameters["access_token"];
            int expires;
            var expiresOn = int.TryParse(querystringParameters["expires_on"], out expires)
                                ? DateTime.UtcNow.AddSeconds(expires)
                                : DateTime.MinValue;

            if (string.IsNullOrEmpty(accessToken) ||
                expiresOn <= DateTime.UtcNow)
            {
                throw new AuthenticationException(
                    "Retrieved a Facebook Access Token but it doesn't contain both the access_token and expires_on parameters.");
            }

            return accessToken;
        }

        private UserInformation RetrieveMe(string accessToken)
        {
            Condition.Requires(accessToken).IsNotNull();

            IRestResponse<MeResult> response;

            try
            {
                var restRequest = new RestRequest("me");
                restRequest.AddParameter("access_token", accessToken);

                response = _restClient.Execute<MeResult>(restRequest);
            }
            catch (Exception exception)
            {
                throw new AuthenticationException("Failed to retrieve any Me data from the Facebook Api.", exception);
            }

            if (response == null ||
                response.StatusCode != HttpStatusCode.OK)
            {
                throw new AuthenticationException(
                    string.Format(
                        "Failed to obtain some Me data from the Facebook api OR the the response was not an HTTP Status 200 OK. Response Status: {0}. Response Description: {1}",
                        response == null ? "-- null response --" : response.StatusCode.ToString(),
                        response == null ? string.Empty : response.StatusDescription));
            }

            return new UserInformation
                   {
                       Id = response.Data.Id,
                       Name = (response.Data.FirstName + " " + response.Data.LastName).Trim(),
                       Locale = response.Data.Locale,
                       UserName = response.Data.Username
                   };
        }

        #region Implementation of IAuthenticationProvider

        public RedirectResult RedirectToAuthenticate(string state)
        {
            Condition.Requires(state).IsNotNullOrEmpty();

            var oauthDialogUri = string.Format("https://www.facebook.com/dialog/oauth?client_id={0}&redirect_uri={1}",
                                               _clientId, _redirectUri.AbsoluteUri);

            // Do we have any scope options?
            if (_scope != null && _scope.Count > 0)
            {
                oauthDialogUri += string.Format(ScopeKey, string.Join(",", _scope));
            }

            oauthDialogUri += string.Format(StateKey, state);

            return new RedirectResult(oauthDialogUri);
        }

        public IAuthenticatedClient AuthenticateClient(NameValueCollection parameters, string existingState)
        {
            Condition.Requires(parameters).IsNotNull().IsLongerThan(0);
            Condition.Requires(existingState).IsNotNull();

            // Is this a facebook callback?
            var code = parameters["code"];
            var state = parameters["state"];

            if (!string.IsNullOrEmpty(code) &&
                !string.IsNullOrEmpty(state))
            {
                var authenticatedClient = new AuthenticatedClient(ProviderType.Facebook);

                // CSRF (state) check.
                if (state != existingState)
                {
                    authenticatedClient.ErrorInformation =
                        new ErrorInformation(
                            "The states do not match. It's possible that you may be a victim of a CSRF.");
                    return authenticatedClient;
                }

                try
                {
                    authenticatedClient.AccessToken = RetrieveAccessToken(code);
                    authenticatedClient.UserInformation = RetrieveMe(authenticatedClient.AccessToken);
                }
                catch (Exception exception)
                {
                    authenticatedClient.ErrorInformation = new ErrorInformation
                                                           {
                                                               Message = exception.Message,
                                                               Exception = exception
                                                           };
                }

                return authenticatedClient;
            }

            // Maybe we have an error?
            var errorReason = parameters["error_reason"];
            var error = parameters["error"];
            var errorDescription = parameters["error_description"];
            if (!string.IsNullOrEmpty(errorReason) &&
                !string.IsNullOrEmpty(error) &&
                !string.IsNullOrEmpty(errorDescription))
            {
                return new AuthenticatedClient(ProviderType.Facebook)
                       {
                           ErrorInformation =
                               new ErrorInformation(string.Format("Reason: {0}. Error: {1}. Description: {2}.",
                                                                  errorReason,
                                                                  error,
                                                                  errorDescription))
                       };
            }

            // Not any facebook params.
            return null;
        }

        #endregion
    }
}