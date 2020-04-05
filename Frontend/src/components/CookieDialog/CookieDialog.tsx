import React, { useState } from "react";
import styles from "./CookieDialog.module.scss";
import { Link, useLocation } from "react-router-dom";
import { Button } from "../Button/Button";
import { Container, FormControlLabel, Checkbox, FormGroup } from "@material-ui/core";
import { AllowedConsents, Consent, ConsentDescription, useConsent } from "../../providers/CookieConsent";
import { useFormik, Form, FormikProvider } from "formik";

const initialConsentState = AllowedConsents.map(c => c === "basics" ? { key: c, value: true } : { key: c, value: false })
                                           .reduce((map: { [id: string] : boolean }, { key, value }) => {
                                               map[key] = value;
                                               return map;
                                           }, {});

export default () => {
    const { consent, setConsent, setAllowAllConsent } = useConsent();
    const [ showMoreOptions, setShowMoreOptions ] = useState(false);
    const location = useLocation();
    const alwaysShow = location.pathname === "/privacy-policy";
    const formik = useFormik({
        initialValues: initialConsentState,
        onSubmit: (values) => {
            const selectedConsents = 
                Object.entries(values)
                      .filter(([_k, v]) => v)
                      .map(([k, _v]) => k as Consent);
            setConsent(selectedConsents);
        }
    });

    if (consent.length > 0 && !alwaysShow) {
        return null;
    }

    return <div className={styles.cookieDialog}>
        <Container>
            Thank you for visiting our website!
            Please note that our website uses cookies to analyze and improve the performance of our website and to make social media integration possible.
            For more information on the use of cookies and privacy related matters, please see our <Link to="/privacy-policy">Privacy and Cookies Policy</Link>.
            By clicking "Accept", you consent to the use of cookies, analytics and social-media integration.
            Click on "More options" to customize your choice.
            If you want to change your choice later, please visit our <Link to="/privacy-policy">privacy policy</Link> where you can edit your choices afterwards.
            { showMoreOptions ?
                <div>
                    <FormikProvider value={formik}>
                        <Form onSubmit={formik.submitForm}>
                            {
                                AllowedConsents.map((c: Consent) => 
                                    <FormGroup key={c}>
                                        <FormControlLabel
                                            control={<Checkbox
                                                name={c}
                                                checked={formik.values[c]}
                                                {...formik.getFieldProps(c)}
                                            />}
                                            label={ConsentDescription(c)}
                                        />
                                    </FormGroup>)
                            }
                            <Button type="submit" disabled={formik.isSubmitting} onClick={() => formik.submitForm()}>Accept</Button>
                            <Button type="button" disabled={formik.isSubmitting} onClick={() => setShowMoreOptions(false)}>Less options</Button>
                        </Form>
                    </FormikProvider>
                </div> :
                <div>
                    <Button onClick={() => setAllowAllConsent()}>Accept</Button>
                    <Button onClick={() => setShowMoreOptions(true)}>More options</Button>
                </div>
            }
        </Container>
    </div>;
}; 