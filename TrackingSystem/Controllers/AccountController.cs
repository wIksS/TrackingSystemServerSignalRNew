﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.ModelBinding;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.EntityFramework;
using Microsoft.AspNet.Identity.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OAuth;
using TrackingSystem.Models;
using TrackingSystem.Providers;
using TrackingSystem.Results;
using System.Web.Http.Cors;
using TrackingSystem.Data;
using System.Net;
using System.Web.Security;
using TrackingSystem.ViewModels;
using AutoMapper;

namespace TrackingSystem.Controllers
{
    //[EnableCors(origins: "*", headers: "*", methods: "*")]
    [Authorize]
    [RoutePrefix("api/Account")]
    public class AccountController : BaseController
    {
        private const string LocalLoginProvider = "Local";
        private ApplicationUserManager _userManager;

        public AccountController()
            : base(new TrackingSystemData())
        {
        }

        public AccountController(ITrackingSystemData data)
            : base(data)
        {
        }

        public AccountController(ApplicationUserManager userManager,
            ISecureDataFormat<AuthenticationTicket> accessTokenFormat, ITrackingSystemData data)
            : base(data)
        {
            UserManager = userManager;
            AccessTokenFormat = accessTokenFormat;
        }

        public ApplicationUserManager UserManager
        {
            get
            {
                return _userManager ?? Request.GetOwinContext().GetUserManager<ApplicationUserManager>();
            }
            private set
            {
                _userManager = value;
            }
        }

        public ISecureDataFormat<AuthenticationTicket> AccessTokenFormat { get; private set; }


        [AllowAnonymous]
        public HttpResponseMessage Options()
        {
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK
            };
        }

        // GET api/Account/UserInfo
        [HostAuthentication(DefaultAuthenticationTypes.ExternalBearer)]
        [Route("UserInfo")]
        public UserInfoViewModel GetUserInfo()
        {
            ExternalLoginData externalLogin = ExternalLoginData.FromIdentity(User.Identity as ClaimsIdentity);

            return new UserInfoViewModel
            {
                Email = User.Identity.GetUserName(),
                HasRegistered = externalLogin == null,
                LoginProvider = externalLogin != null ? externalLogin.LoginProvider : null
            };
        }

        // POST api/Account/Logout
        [Route("Logout")]
        public IHttpActionResult Logout()
        {
            Authentication.SignOut(CookieAuthenticationDefaults.AuthenticationType);
            return Ok();
        }

        // GET api/Account/ManageInfo?returnUrl=%2F&generateState=true
        [Route("ManageInfo")]
        public async Task<ManageInfoViewModel> GetManageInfo(string returnUrl, bool generateState = false)
        {
            IdentityUser user = await UserManager.FindByIdAsync(User.Identity.GetUserId());

            if (user == null)
            {
                return null;
            }

            List<UserLoginInfoViewModel> logins = new List<UserLoginInfoViewModel>();

            foreach (IdentityUserLogin linkedAccount in user.Logins)
            {
                logins.Add(new UserLoginInfoViewModel
                {
                    LoginProvider = linkedAccount.LoginProvider,
                    ProviderKey = linkedAccount.ProviderKey
                });
            }

            if (user.PasswordHash != null)
            {
                logins.Add(new UserLoginInfoViewModel
                {
                    LoginProvider = LocalLoginProvider,
                    ProviderKey = user.UserName,
                });
            }

            return new ManageInfoViewModel
            {
                LocalLoginProvider = LocalLoginProvider,
                Email = user.UserName,
                Logins = logins,
                ExternalLoginProviders = GetExternalLogins(returnUrl, generateState)
            };
        }

        // POST api/Account/ChangePassword
        [Route("ChangePassword")]
        public async Task<IHttpActionResult> ChangePassword(ChangePasswordBindingModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            IdentityResult result = await UserManager.ChangePasswordAsync(User.Identity.GetUserId(), model.OldPassword,
                model.NewPassword);

            if (!result.Succeeded)
            {
                return GetErrorResult(result);
            }

            return Ok();
        }

        // POST api/Account/SetPassword
        [Route("SetPassword")]
        public async Task<IHttpActionResult> SetPassword(SetPasswordBindingModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            IdentityResult result = await UserManager.AddPasswordAsync(User.Identity.GetUserId(), model.NewPassword);

            if (!result.Succeeded)
            {
                return GetErrorResult(result);
            }

            return Ok();
        }

        // POST api/Account/AddExternalLogin
        [Route("AddExternalLogin")]
        public async Task<IHttpActionResult> AddExternalLogin(AddExternalLoginBindingModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            Authentication.SignOut(DefaultAuthenticationTypes.ExternalCookie);

            AuthenticationTicket ticket = AccessTokenFormat.Unprotect(model.ExternalAccessToken);

            if (ticket == null || ticket.Identity == null || (ticket.Properties != null
                && ticket.Properties.ExpiresUtc.HasValue
                && ticket.Properties.ExpiresUtc.Value < DateTimeOffset.UtcNow))
            {
                return BadRequest("External login failure.");
            }

            ExternalLoginData externalData = ExternalLoginData.FromIdentity(ticket.Identity);

            if (externalData == null)
            {
                return BadRequest("The external login is already associated with an account.");
            }

            IdentityResult result = await UserManager.AddLoginAsync(User.Identity.GetUserId(),
                new UserLoginInfo(externalData.LoginProvider, externalData.ProviderKey));

            if (!result.Succeeded)
            {
                return GetErrorResult(result);
            }

            return Ok();
        }

        // POST api/Account/RemoveLogin
        [Route("RemoveLogin")]
        public async Task<IHttpActionResult> RemoveLogin(RemoveLoginBindingModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            IdentityResult result;

            if (model.LoginProvider == LocalLoginProvider)
            {
                result = await UserManager.RemovePasswordAsync(User.Identity.GetUserId());
            }
            else
            {
                result = await UserManager.RemoveLoginAsync(User.Identity.GetUserId(),
                    new UserLoginInfo(model.LoginProvider, model.ProviderKey));
            }

            if (!result.Succeeded)
            {
                return GetErrorResult(result);
            }

            return Ok();
        }

        // GET api/Account/ExternalLogin
        [OverrideAuthentication]
        [HostAuthentication(DefaultAuthenticationTypes.ExternalCookie)]
        [AllowAnonymous]
        [Route("ExternalLogin", Name = "ExternalLogin")]
        public async Task<IHttpActionResult> GetExternalLogin(string provider, string error = null)
        {
            if (error != null)
            {
                return Redirect(Url.Content("~/") + "#error=" + Uri.EscapeDataString(error));
            }

            if (!User.Identity.IsAuthenticated)
            {
                return new ChallengeResult(provider, this);
            }

            ExternalLoginData externalLogin = ExternalLoginData.FromIdentity(User.Identity as ClaimsIdentity);

            if (externalLogin == null)
            {
                return InternalServerError();
            }

            if (externalLogin.LoginProvider != provider)
            {
                Authentication.SignOut(DefaultAuthenticationTypes.ExternalCookie);
                return new ChallengeResult(provider, this);
            }

            ApplicationUser user = await UserManager.FindAsync(new UserLoginInfo(externalLogin.LoginProvider,
                externalLogin.ProviderKey));

            bool hasRegistered = user != null;

            if (hasRegistered)
            {
                Authentication.SignOut(DefaultAuthenticationTypes.ExternalCookie);

                ClaimsIdentity oAuthIdentity = await user.GenerateUserIdentityAsync(UserManager,
                   OAuthDefaults.AuthenticationType);
                ClaimsIdentity cookieIdentity = await user.GenerateUserIdentityAsync(UserManager,
                    CookieAuthenticationDefaults.AuthenticationType);

                AuthenticationProperties properties = ApplicationOAuthProvider.CreateProperties(user.UserName);
                Authentication.SignIn(properties, oAuthIdentity, cookieIdentity);
            }
            else
            {
                IEnumerable<Claim> claims = externalLogin.GetClaims();
                ClaimsIdentity identity = new ClaimsIdentity(claims, OAuthDefaults.AuthenticationType);
                Authentication.SignIn(identity);
            }

            return Ok();
        }

        // GET api/Account/ExternalLogins?returnUrl=%2F&generateState=true
        [AllowAnonymous]
        [Route("ExternalLogins")]
        public IEnumerable<ExternalLoginViewModel> GetExternalLogins(string returnUrl, bool generateState = false)
        {
            IEnumerable<AuthenticationDescription> descriptions = Authentication.GetExternalAuthenticationTypes();
            List<ExternalLoginViewModel> logins = new List<ExternalLoginViewModel>();

            string state;

            if (generateState)
            {
                const int strengthInBits = 256;
                state = RandomOAuthStateGenerator.Generate(strengthInBits);
            }
            else
            {
                state = null;
            }

            foreach (AuthenticationDescription description in descriptions)
            {
                ExternalLoginViewModel login = new ExternalLoginViewModel
                {
                    Name = description.Caption,
                    Url = Url.Route("ExternalLogin", new
                    {
                        provider = description.AuthenticationType,
                        response_type = "token",
                        client_id = Startup.PublicClientId,
                        redirect_uri = new Uri(Request.RequestUri, returnUrl).AbsoluteUri,
                        state = state
                    }),
                    State = state
                };
                logins.Add(login);
            }

            return logins;
        }

        // POST api/Account/Register
        [AllowAnonymous]
        [Route("Register")]
        public async Task<IHttpActionResult> Register(RegisterBindingModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            ApplicationUser user;
            var isTeacher = false;

            if (model.IsTeacher)
            {
                user = new Teacher() { UserName = model.Email, Email = model.Email, Phone = model.Phone };
                isTeacher = true;
            }
            else
            {
                user = new Student() { UserName = model.Email, Email = model.Email, Phone = model.Phone };
            }


            IdentityResult result = await UserManager.CreateAsync(user, model.Password);

            if (!result.Succeeded)
            {
                return GetErrorResult(result);
            }

            if (isTeacher)
            {
                this.UserManager.AddToRole(user.Id, "Teacher");
            }

            return Ok();
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        [Route("GetUser")]
        public IHttpActionResult GetUser(string id)
        {
            ApplicationUser user = Data.Users.Find(id);
            var userViewModel = Mapper.Map<ApplicationUserViewModel>(user);

            return Ok(userViewModel);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [Route("UpdateUser")]
        public IHttpActionResult UpdateUser(RegisterBindingModel model)
        {
            ApplicationUser user = Data.Users.Find(model.Id);
            if (user != null)
            {
                user.Email = model.Email;

                UserManager.Update(user);
                UserManager.ChangePassword(model.Id, user.PasswordHash,model.Password);
            }

            return Ok(user);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        [Route("Users")]
        public IHttpActionResult GetAllUsers()
        {
            var usersViewModel = new List<UserViewModel>();
            foreach (var user in this.Data.Users.All().ToList())
            {
                if (user.Id != User.Identity.GetUserId())
                {
                    usersViewModel.Add(new UserViewModel()
                    {
                        UserName = user.UserName,
                        Id = user.Id,
                        Roles = this.UserManager.GetRoles(user.Id)
                    });
                }
            }

            return Ok(usersViewModel);
        }

        [HttpDelete]
        [Authorize(Roles = "Admin")]
        [Route("DeleteUser")]
        public IHttpActionResult DeleteUser(string userName)
        {
            ApplicationUser user = this.UserManager.Users.FirstOrDefault(u => u.UserName == userName);
            if (user == null)
            {
                return BadRequest();
            }

            this.UserManager.Delete(user);

            return Ok("Successfuly deleted user !");
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [Route("AddRole")]
        public IHttpActionResult AddRole(string userName, string roleName)
        {
            ApplicationUser userDb = this.Data.Users.All().FirstOrDefault(u => u.UserName == userName);

            if (userDb == null)
            {
                return BadRequest();
            }

            ApplicationUser user = this.UserManager.Users.FirstOrDefault(u => u.UserName == userName);
            this.UserManager.AddToRole(user.Id, roleName);

            // this is a hack for changing the whole role of an user. It is needed because it is needed to cast a derrived class to derrived class, which is impossible and to do that i ahve to delete the entities
            if (userDb is Student && roleName == "Teacher")
            {
                var coordinates = user.Coordinates;
                var coordinatesList = coordinates.ToList();
                for (int i = 0; i < coordinatesList.Count; i++)
                {
                    coordinatesList[i].User = null;
                }

                var group = user.Group;
                var image = user.ImageUrl;
                user.Group = null;

                this.UserManager.Delete(user);

                var curTteacher = new Teacher() { UserName = user.Email, Email = user.Email, PasswordHash = user.PasswordHash };

                curTteacher.Coordinates = coordinates;
                curTteacher.ImageUrl = image;

                if (group != null)
                {
                    curTteacher.Group = group;
                    curTteacher.GroupId = group.Id;
                }

                UserManager.Create(curTteacher);
                UserManager.AddToRole(curTteacher.Id, "Teacher");

                for (int i = 0; i < coordinatesList.Count; i++)
                {
                    coordinatesList[i].User = curTteacher;
                }

                Data.Coordinates.SaveChanges();
            }

            return Ok("Role created successfully !");
        }

        [HttpDelete]
        [Authorize(Roles = "Admin")]
        [Route("DeleteRole")]
        public IHttpActionResult DeleteRole(string userName, string roleName)
        {
            ApplicationUser user = this.UserManager.Users.FirstOrDefault(u => u.UserName == userName);
            if (user == null)
            {
                return BadRequest();
            }

            this.UserManager.RemoveFromRole(user.Id, roleName);
            if (user is Teacher && roleName == "Teacher")
            {
                var coordinates = user.Coordinates;
                var coordinatesList = coordinates.ToList();
                for (int i = 0; i < coordinatesList.Count; i++)
                {
                    coordinatesList[i].User = null;
                }

                var group = user.Group;
                var image = user.ImageUrl;
                user.Group = null;

                this.UserManager.Delete(user);

                var curStudent = new Student() { UserName = user.Email, Email = user.Email, PasswordHash = user.PasswordHash };

                curStudent.Coordinates = coordinates;
                curStudent.ImageUrl = image;
                if (group != null)
                {
                    curStudent.Group = group;
                    curStudent.GroupId = group.Id;
                }

                UserManager.Create(curStudent);
                for (int i = 0; i < coordinatesList.Count; i++)
                {
                    coordinatesList[i].User = curStudent;
                }

                Data.Coordinates.SaveChanges();

            }

            return Ok("Role deleted successfully !");
        }

        [HttpGet]
        [Authorize]
        [Route("GetRoles")]
        public ICollection<string> GetRoles()
        {
            var userId = User.Identity.GetUserId();
            ApplicationUser user = this.UserManager.Users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
            {
                return null;
            }

            return this.UserManager.GetRoles(userId);
        }

        // POST api/Account/RegisterExternal
        //[OverrideAuthentication]
        //[HostAuthentication(DefaultAuthenticationTypes.ExternalBearer)]
        //[Route("RegisterExternal")]
        //public async Task<IHttpActionResult> RegisterExternal(RegisterExternalBindingModel model)
        //{
        //    if (!ModelState.IsValid)
        //    {
        //        return BadRequest(ModelState);
        //    }

        //    var info = await Authentication.GetExternalLoginInfoAsync();
        //    if (info == null)
        //    {
        //        return InternalServerError();
        //    }


        //    var user = new ApplicationUser() { UserName = model.Email, Email = model.Email };

        //    IdentityResult result = await UserManager.CreateAsync(user);
        //    if (!result.Succeeded)
        //    {
        //        return GetErrorResult(result);
        //    }

        //    result = await UserManager.AddLoginAsync(user.Id, info.Login);
        //    if (!result.Succeeded)
        //    {
        //        return GetErrorResult(result); 
        //    }
        //    return Ok();
        //}

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UserManager.Dispose();
            }

            base.Dispose(disposing);
        }

        #region Helpers

        private IAuthenticationManager Authentication
        {
            get { return Request.GetOwinContext().Authentication; }
        }

        private IHttpActionResult GetErrorResult(IdentityResult result)
        {
            if (result == null)
            {
                return InternalServerError();
            }

            if (!result.Succeeded)
            {
                if (result.Errors != null)
                {
                    foreach (string error in result.Errors)
                    {
                        ModelState.AddModelError("", error);
                    }
                }

                if (ModelState.IsValid)
                {
                    // No ModelState errors are available to send, so just return an empty BadRequest.
                    return BadRequest();
                }

                return BadRequest(ModelState);
            }

            return null;
        }

        private class ExternalLoginData
        {
            public string LoginProvider { get; set; }
            public string ProviderKey { get; set; }
            public string UserName { get; set; }

            public IList<Claim> GetClaims()
            {
                IList<Claim> claims = new List<Claim>();
                claims.Add(new Claim(ClaimTypes.NameIdentifier, ProviderKey, null, LoginProvider));

                if (UserName != null)
                {
                    claims.Add(new Claim(ClaimTypes.Name, UserName, null, LoginProvider));
                }

                return claims;
            }

            public static ExternalLoginData FromIdentity(ClaimsIdentity identity)
            {
                if (identity == null)
                {
                    return null;
                }

                Claim providerKeyClaim = identity.FindFirst(ClaimTypes.NameIdentifier);

                if (providerKeyClaim == null || String.IsNullOrEmpty(providerKeyClaim.Issuer)
                    || String.IsNullOrEmpty(providerKeyClaim.Value))
                {
                    return null;
                }

                if (providerKeyClaim.Issuer == ClaimsIdentity.DefaultIssuer)
                {
                    return null;
                }

                return new ExternalLoginData
                {
                    LoginProvider = providerKeyClaim.Issuer,
                    ProviderKey = providerKeyClaim.Value,
                    UserName = identity.FindFirstValue(ClaimTypes.Name)
                };
            }
        }

        private static class RandomOAuthStateGenerator
        {
            private static RandomNumberGenerator _random = new RNGCryptoServiceProvider();

            public static string Generate(int strengthInBits)
            {
                const int bitsPerByte = 8;

                if (strengthInBits % bitsPerByte != 0)
                {
                    throw new ArgumentException("strengthInBits must be evenly divisible by 8.", "strengthInBits");
                }

                int strengthInBytes = strengthInBits / bitsPerByte;

                byte[] data = new byte[strengthInBytes];
                _random.GetBytes(data);
                return HttpServerUtility.UrlTokenEncode(data);
            }
        }

        #endregion
    }
}
