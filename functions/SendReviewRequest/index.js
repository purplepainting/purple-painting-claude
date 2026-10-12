const https = require('https');

module.exports = async function (context, req) {
    context.log('Review request webhook received');

    const body = req.body;
    if (!body) {
        context.res = { status: 400, body: "No data received" };
        return;
    }

    const accountId = body.accountId;
    const locationId = body.locationId;

    if (!accountId) {
        context.res = { status: 400, body: "No account ID provided" };
        return;
    }

    try {
        const [contact, streetAddress] = await Promise.all([
            getCustomerContact(accountId, context),
            locationId ? getStreetAddress(locationId, context) : Promise.resolve(null)
        ]);

        if (!contact || !contact.email) {
            context.log('No email found for account: ' + accountId);
            context.res = { status: 200, body: "No email found, skipping" };
            return;
        }

        const customerEmail = contact.email;
        const customerName = contact.name || 'Valued Customer';

        const token = await getAccessToken();
        await sendEmail(token, customerName, customerEmail, streetAddress);

        context.log(`Review request sent to ${customerEmail}`);
        context.res = { status: 200, body: "Email sent successfully to " + customerEmail };
    } catch (error) {
        context.log.error('Failed:', error);
        context.res = { status: 500, body: "Failed: " + error.message };
    }
};

async function getStreetAddress(locationId, context) {
    const apiKey = process.env.JOBTREAD_API_KEY;

    const payload = JSON.stringify({
        query: {
            $: { grantKey: apiKey },
            location: {
                $: { id: locationId },
                id: {},
                address: {}
            }
        }
    });

    return new Promise((resolve, reject) => {
        const options = {
            hostname: 'api.jobtread.com',
            path: '/pave',
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(payload)
            }
        };

        const req = https.request(options, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                try {
                    context.log('Location response: ' + data);
                    const parsed = JSON.parse(data);
                    const fullAddress = parsed?.location?.address;
                    if (!fullAddress) {
                        resolve(null);
                        return;
                    }
                    const street = fullAddress.split(',')[0].trim();
                    resolve(street);
                } catch(e) {
                    context.log('Failed to parse location: ' + data);
                    resolve(null);
                }
            });
        });
        req.on('error', reject);
        req.write(payload);
        req.end();
    });
}

async function getCustomerContact(accountId, context) {
    const apiKey = process.env.JOBTREAD_API_KEY;

    const payload = JSON.stringify({
        query: {
            $: { grantKey: apiKey },
            account: {
                $: { id: accountId },
                id: {},
                name: {},
                contacts: {
                    nodes: {
                        id: {},
                        name: {},
                        customFieldValues: {
                            nodes: {
                                id: {},
                                value: {},
                                customField: {
                                    id: {},
                                    name: {}
                                }
                            }
                        }
                    }
                }
            }
        }
    });

    return new Promise((resolve, reject) => {
        const options = {
            hostname: 'api.jobtread.com',
            path: '/pave',
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(payload)
            }
        };

        const req = https.request(options, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                try {
                    context.log('JobTread response: ' + data);
                    const parsed = JSON.parse(data);
                    const contacts = parsed?.account?.contacts?.nodes || [];
                    const primary = contacts[0];
                    if (primary) {
                        const emailField = primary.customFieldValues?.nodes?.find(
                            f => f.customField?.name?.toLowerCase() === 'email'
                        );
                        resolve(emailField ? { name: primary.name, email: emailField.value } : null);
                    } else {
                        resolve(null);
                    }
                } catch(e) {
                    reject(new Error('Failed to parse JobTread response: ' + data));
                }
            });
        });
        req.on('error', reject);
        req.write(payload);
        req.end();
    });
}

async function getAccessToken() {
    const tenantId = process.env.AZURE_TENANT_ID;
    const clientId = process.env.AZURE_CLIENT_ID;
    const clientSecret = process.env.AZURE_CLIENT_SECRET;

    const postData = new URLSearchParams({
        grant_type: 'client_credentials',
        client_id: clientId,
        client_secret: clientSecret,
        scope: 'https://graph.microsoft.com/.default'
    }).toString();

    return new Promise((resolve, reject) => {
        const options = {
            hostname: 'login.microsoftonline.com',
            path: `/${tenantId}/oauth2/v2.0/token`,
            method: 'POST',
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded',
                'Content-Length': Buffer.byteLength(postData)
            }
        };

        const req = https.request(options, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                const parsed = JSON.parse(data);
                if (parsed.access_token) resolve(parsed.access_token);
                else reject(new Error('Token error: ' + JSON.stringify(parsed)));
            });
        });
        req.on('error', reject);
        req.write(postData);
        req.end();
    });
}

async function sendEmail(token, customerName, customerEmail, streetAddress) {
    const senderEmail = process.env.EMAIL_USER;

    const addressLine = streetAddress
        ? `your property at <strong>${streetAddress}</strong>`
        : `your property`;

const subjectLine = streetAddress
    ? `Purple Painting: How did we do at ${streetAddress}? ⭐`
    : `Purple Painting: How did we do? ⭐`;

    const emailBody = {
        message: {
            subject: subjectLine,
            body: {
                contentType: 'HTML',
                content: `
                    <div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;">
                        <p>Hi ${customerName},</p>
                        <p>We just wanted to take a moment to say <strong>thank you</strong> for trusting Purple Painting Co. with the work at ${addressLine}. It was truly a pleasure, and we hope you're absolutely loving the results.</p>
                        <p>Every project means a lot to us — not just the walls we paint, but the trust you place in our team to show up, do the job right, and leave your home looking beautiful. That means everything to us.</p>
                        <p>If you have a few minutes, we'd love to hear about your experience. Your review helps other Santa Barbara homeowners find us — and honestly, hearing from happy customers is the best part of what we do.</p>
                        <div style="text-align: center; margin: 30px 0;">
                            <a href="https://g.page/r/Cd2xOd5HhouuEBM/review"
                               style="background-color: #2C1654; color: #C8963E; padding: 15px 30px;
                                      text-decoration: none; border-radius: 5px; font-weight: bold; font-size: 16px;">
                                ⭐ Leave Us a Google Review
                            </a>
                        </div>
                        <p>Thank you again — we truly appreciate your business and your trust in our team.</p>
                        <p>Warm regards,<br/>
                        <strong>Kareem Mustafa, Owner</strong><br/>
                        Purple Painting<br/>
                        (805) 448-1241</p>
                    </div>
                `
            },
            toRecipients: [
                { emailAddress: { address: customerEmail } }
            ]
        },
        saveToSentItems: true
    };

    const postData = JSON.stringify(emailBody);

    return new Promise((resolve, reject) => {
        const options = {
            hostname: 'graph.microsoft.com',
            path: `/v1.0/users/${senderEmail}/sendMail`,
            method: 'POST',
            headers: {
                'Authorization': 'Bearer ' + token,
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(postData)
            }
        };

        const req = https.request(options, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                if (res.statusCode === 202) resolve();
                else reject(new Error(`Graph API error ${res.statusCode}: ${data}`));
            });
        });
        req.on('error', reject);
        req.write(postData);
        req.end();
    });
}