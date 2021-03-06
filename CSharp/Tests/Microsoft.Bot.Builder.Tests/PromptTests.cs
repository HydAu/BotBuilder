﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using Microsoft.Bot.Connector;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Internals;
using Microsoft.Bot.Builder.Internals.Fibers;

namespace Microsoft.Bot.Builder.Tests
{
    public abstract class PromptTests_Base
    {
        public static string NewID()
        {
            return Guid.NewGuid().ToString();
        }

        public interface IPromptCaller<T> : IDialog
        {
            Task FirstMessage(IDialogContext context, IAwaitable<Connector.Message> message);
            Task PromptResult(IDialogContext context, IAwaitable<T> result);
        }

        public static Mock<IPromptCaller<T>> MockDialog<T>(string id = null)
        {
            var dialog = new Moq.Mock<IPromptCaller<T>>(MockBehavior.Loose);
            id = id ?? NewID();
            dialog.Setup(d => d.ToString()).Returns(id);
            return dialog;
        }

        public static async Task<DialogContext> MakeContextAsync(IDialog root, IBotToUser botToUser)
        {
            var data = new JObjectBotData(new Connector.Message());
            IFiberLoop fiber = new Fiber(new FrameFactory(new WaitFactory()));
            var context = new DialogContext(botToUser, data, fiber);
            var loop = Methods.Void(Methods.Loop(context.ToRest(root.StartAsync), int.MaxValue));
            fiber.Call(loop, null);
            await fiber.PollAsync();
            return context;
        }
    }


    [TestClass]
    public sealed class PromptTests_Success : PromptTests_Base
    {
        private const string PromptText = "hello there";

        public async Task PromptSuccessAsync<T>(Action<IDialogContext, ResumeAfter<T>> prompt, string text, T expected)
        {
            var dialogRoot = MockDialog<T>();

            dialogRoot
                .Setup(d => d.StartAsync(It.IsAny<IDialogContext>()))
                .Returns<IDialogContext>(async c => { c.Wait(dialogRoot.Object.FirstMessage); });
            dialogRoot
                .Setup(d => d.FirstMessage(It.IsAny<IDialogContext>(), It.IsAny<IAwaitable<Connector.Message>>()))
                .Returns<IDialogContext, IAwaitable<object>>(async (c, a) => { prompt(c, dialogRoot.Object.PromptResult); });

            var conversationID = NewID();
            var botToUser = new BotToUserQueue(new Message() { ConversationId = conversationID });
            var context = await MakeContextAsync(dialogRoot.Object, botToUser);
            IUserToBot userToBot = context;
            {
                var toBot = new Connector.Message() { ConversationId = conversationID };
                botToUser.Clear();
                await userToBot.SendAsync(toBot);
                var toUser = botToUser.Messages.Single();
                Assert.AreEqual(PromptText, toUser.Text);
            }

            {
                var toBot = new Connector.Message() { ConversationId = conversationID, Text = text };
                botToUser.Clear();
                await userToBot.SendAsync(toBot);
                Assert.IsFalse(botToUser.Messages.Any());
                dialogRoot.Verify(d => d.PromptResult(context, It.Is<IAwaitable<T>>(actual => actual.GetAwaiter().GetResult().Equals(expected))), Times.Once);
            }
        }

        [TestMethod]
        public async Task PromptSuccess_Text()
        {
            await PromptSuccessAsync((context, resume) => PromptDialog.Text(context, resume, PromptText), "lol wut", "lol wut");
        }

        [TestMethod]
        public async Task PromptSuccess_Confirm_Yes()
        {
            await PromptSuccessAsync((context, resume) => PromptDialog.Confirm(context, resume, PromptText), "yes", true);
        }

        [TestMethod]
        public async Task PromptSuccess_Confirm_No()
        {
            await PromptSuccessAsync((context, resume) => PromptDialog.Confirm(context, resume, PromptText), "no", false);
        }

        [TestMethod]
        public async Task PromptSuccess_Number_Long()
        {
            await PromptSuccessAsync((context, resume) => PromptDialog.Number(context, resume, PromptText), "42", 42L);
        }

        [TestMethod]
        public async Task PromptSuccess_Number_Double()
        {
            await PromptSuccessAsync((context, resume) => PromptDialog.Number(context, resume, PromptText), "42", 42d);
        }

        [TestMethod]
        public async Task PromptSuccess_Choice()
        {
            var choices = new[] { "one", "two", "three" };
            await PromptSuccessAsync((context, resume) => PromptDialog.Choice(context, resume, choices, PromptText), "two", "two");
        }
    }

    [TestClass]
    public sealed class PromptTests_Failure : PromptTests_Base
    {
        private const string PromptText = "hello there";
        private const string RetryText = "hello there again";
        private const int MaximumAttempts = 2;

        public async Task PromptFailureAsync<T>(Action<IDialogContext, ResumeAfter<T>> prompt)
        {
            var dialogRoot = MockDialog<T>();

            dialogRoot
                .Setup(d => d.StartAsync(It.IsAny<IDialogContext>()))
                .Returns<IDialogContext>(async c => { c.Wait(dialogRoot.Object.FirstMessage); });
            dialogRoot
                .Setup(d => d.FirstMessage(It.IsAny<IDialogContext>(), It.IsAny<IAwaitable<Connector.Message>>()))
                .Returns<IDialogContext, IAwaitable<object>>(async (c, a) => { prompt(c, dialogRoot.Object.PromptResult); });

            var conversationID = NewID();
            var botToUser = new BotToUserQueue(new Message() { ConversationId = conversationID });
            var context = await MakeContextAsync(dialogRoot.Object, botToUser);

            IUserToBot userToBot = context;
            {
                var toBot = new Connector.Message() { ConversationId = conversationID };
                botToUser.Clear();
                await userToBot.SendAsync(toBot);
                var toUser = botToUser.Messages.Single();
                Assert.AreEqual(PromptText, toUser.Text);
            }
            {
                var toBot = new Connector.Message() { ConversationId = conversationID };
                botToUser.Clear();
                await userToBot.SendAsync(toBot);
                var toUser = botToUser.Messages.Single();
                Assert.AreEqual(RetryText, toUser.Text);
            }

            {
                var toBot = new Connector.Message() { ConversationId = conversationID };
                botToUser.Clear();
                await userToBot.SendAsync(toBot);
                var toUser = botToUser.Messages.Single();
                dialogRoot.Verify(d => d.PromptResult(context, It.Is<IAwaitable<T>>(actual => actual.ToTask().IsFaulted)), Times.Once);
            }
        }

        [TestMethod]
        public async Task PromptFailure_Number()
        {
            await PromptFailureAsync<long>((context, resume) => PromptDialog.Number(context, resume, PromptText, RetryText, MaximumAttempts));
        }

        [TestMethod]
        public async Task PromptFailure_Choice()
        {
            var choices = new[] { "one", "two", "three" };
            await PromptFailureAsync<string>((context, resume) => PromptDialog.Choice(context, resume, choices, PromptText, RetryText, MaximumAttempts));
        }

        [TestMethod]
        public async Task PromptFailure_Confirm()
        {
            await PromptFailureAsync<bool>((context, resume) => PromptDialog.Confirm(context, resume, PromptText, RetryText, MaximumAttempts));
        }
    }
}
